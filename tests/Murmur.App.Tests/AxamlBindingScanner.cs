using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Data;

namespace Murmur.App.Tests;

/// <summary>
/// Reads the bindings out of the <c>.axaml</c> markup of <c>Murmur.App</c> for the binding-mode guard (#108). Test-only, so it may use
/// reflection: the TwoWay-by-default set comes from Avalonia's own property metadata, and a binding path is walked on the view model named by
/// <c>x:DataType</c> to see whether the setter it would write back through is public.
/// </summary>
internal static class AxamlBindingScanner
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Namespaces searched, in this order, for an element of the default xmlns (the Avalonia one).</summary>
    private static readonly string[] AvaloniaNamespaces =
    [
        "Avalonia.Controls", "Avalonia.Controls.Primitives", "Avalonia.Controls.Documents", "Avalonia.Controls.Shapes",
        "Avalonia.Controls.Presenters", "Avalonia.Controls.Templates", "Avalonia.Input", "Avalonia.Automation", "Avalonia.Media", "Avalonia.Styling", "Avalonia",
    ];

    private static readonly Lazy<Assembly[]> AvaloniaAssemblies = new(() =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is { } n && n.StartsWith("Avalonia", StringComparison.Ordinal)).ToArray());

    private static readonly Dictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);

    public static IEnumerable<string> AxamlFiles() => Directory
        .EnumerateFiles(RepoPaths.AppDirectory, "*.axaml", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal);

    /// <summary>Every binding in the markup on a property that is TwoWay by default or states <c>Mode=TwoWay</c>.</summary>
    public static List<BindingSite> Scan()
    {
        var sites = new List<BindingSite>();
        foreach (var file in AxamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var elementType = ResolveType(element);
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || !IsBinding(attribute.Value))
                    {
                        continue;
                    }

                    var property = attribute.Name.LocalName;
                    var ownerType = elementType;
                    var dot = property.IndexOf('.', StringComparison.Ordinal);
                    if (dot > 0)
                    {
                        ownerType = ResolveType(element, property[..dot]);
                        property = property[(dot + 1)..];
                    }

                    var line = ((IXmlLineInfo)attribute).LineNumber;
                    sites.Add(Describe(file, line, element, elementType, ownerType, property, attribute.Value));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// The properties of <paramref name="type"/> (its own, inherited and attached ones) whose default binding mode is TwoWay, read from
    /// <see cref="AvaloniaPropertyRegistry"/>. Nothing is hand-listed: a new control in the markup is covered as soon as it is used.
    /// </summary>
    public static IReadOnlyList<AvaloniaProperty> TwoWayByDefault(Type type)
    {
        EnsureRegistered(type);
        var registry = AvaloniaPropertyRegistry.Instance;
        return registry.GetRegistered(type).Concat(registry.GetRegisteredDirect(type)).Concat(registry.GetRegisteredAttached(type))
            .Where(p => IsTwoWayByDefault(p, type))
            .DistinctBy(p => (p.OwnerType, p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether a binding on <paramref name="property"/> of <paramref name="type"/> with no stated mode is TwoWay. Judged on the type itself and
    /// on the type that declared the property: Avalonia 12 flips a few of them to OneWay on the subclass (<c>ProgressBar.Value</c> is OneWay,
    /// <c>RangeBase.Value</c> is TwoWay), and a later Avalonia may flip them back, so the stricter answer wins.
    /// </summary>
    private static bool IsTwoWayByDefault(AvaloniaProperty property, Type type) =>
        property.GetMetadata(type).DefaultBindingMode == BindingMode.TwoWay
        || (property.OwnerType.IsAssignableFrom(type) && property.GetMetadata(property.OwnerType).DefaultBindingMode == BindingMode.TwoWay);

    /// <summary>Splits <c>{Binding Path, Mode=OneWay, StringFormat='{}{0}'}</c> into its positional and named arguments.</summary>
    public static (List<string> Positional, Dictionary<string, string> Named) ParseArguments(string markup)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var inner = markup.AsSpan(1, markup.Length - 2);
        var space = inner.IndexOfAny(" \t\r\n");
        if (space < 0)
        {
            return (positional, named);
        }

        var depth = 0;
        var quote = '\0';
        var start = space + 1;
        for (var i = start; i <= inner.Length; i++)
        {
            var c = i < inner.Length ? inner[i] : ',';
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                var argument = inner[start..i].ToString().Trim();
                start = i + 1;
                if (argument.Length == 0)
                {
                    continue;
                }

                var equals = argument.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && !argument.StartsWith('{') && IsIdentifier(argument.AsSpan(0, equals).Trim()))
                {
                    named[argument[..equals].Trim()] = argument[(equals + 1)..].Trim();
                }
                else
                {
                    positional.Add(argument);
                }
            }
        }

        return (positional, named);
    }

    public static bool IsBinding(string text) =>
        text.Length > 2 && text[0] == '{' && text[^1] == '}'
        && (StartsWithExtension(text, "Binding") || StartsWithExtension(text, "CompiledBinding") || StartsWithExtension(text, "ReflectionBinding"));

    private static bool StartsWithExtension(string text, string name) =>
        text.StartsWith("{" + name, StringComparison.Ordinal) && (text.Length == name.Length + 2 || !char.IsLetterOrDigit(text[name.Length + 1]));

    private static BindingSite Describe(string file, int line, XElement element, Type? elementType, Type? ownerType, string property, string markup)
    {
        var (positional, named) = ParseArguments(markup);
        named.TryGetValue("Mode", out var mode);
        var path = named.TryGetValue("Path", out var p) ? p : positional.FirstOrDefault();
        var hasOtherSource = named.ContainsKey("ElementName") || named.ContainsKey("RelativeSource") || named.ContainsKey("Source");

        var defaultIsTwoWay = false;
        if (ownerType is not null)
        {
            EnsureRegistered(ownerType);
            if (elementType is not null)
            {
                EnsureRegistered(elementType);
            }

            var registered = AvaloniaPropertyRegistry.Instance.FindRegistered(ownerType, property)
                             ?? (elementType is null ? null : AvaloniaPropertyRegistry.Instance.FindRegistered(elementType, property));
            if (registered is not null)
            {
                defaultIsTwoWay = IsTwoWayByDefault(registered, elementType ?? ownerType);
            }
        }

        var dataType = hasOtherSource ? null : FindDataType(element);
        var target = hasOtherSource || path is null ? null : ResolvePath(dataType, path);
        return new BindingSite(file, line, elementType?.Name ?? element.Name.LocalName, property, markup, mode, defaultIsTwoWay, dataType, path, target, ownerType is not null);
    }

    /// <summary>Walks <paramref name="path"/> on <paramref name="dataType"/>; <c>null</c> when it cannot be resolved by name.</summary>
    private static PropertyInfo? ResolvePath(Type? dataType, string path)
    {
        if (dataType is null)
        {
            return null;
        }

        PropertyInfo? current = null;
        var type = dataType;
        foreach (var segment in path.Split('.'))
        {
            if (!IsIdentifier(segment) || type is null)
            {
                return null;
            }

            current = type.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (current is null)
            {
                return null;
            }

            type = current.PropertyType;
        }

        return current;
    }

    private static Type? FindDataType(XElement element)
    {
        for (var node = element; node is not null; node = node.Parent)
        {
            var declared = node.Attribute(Xaml + "DataType")?.Value ?? (node.Name.LocalName == "DataTemplate" ? node.Attribute("DataType")?.Value : null);
            if (declared is not null)
            {
                return ResolveTypeName(node, declared);
            }
        }

        return null;
    }

    private static Type? ResolveType(XElement element) => ResolveType(element, element.Name.LocalName, element.Name.NamespaceName);

    private static Type? ResolveType(XElement scope, string name) => ResolveTypeName(scope, name);

    private static Type? ResolveTypeName(XElement scope, string qualified)
    {
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return ResolveType(scope, qualified, Avalonia.NamespaceName);
        }

        var ns = scope.GetNamespaceOfPrefix(qualified[..colon]);
        return ns is null ? null : ResolveType(scope, qualified[(colon + 1)..], ns.NamespaceName);
    }

    private static Type? ResolveType(XElement scope, string name, string xmlNamespace)
    {
        _ = scope;
        var key = xmlNamespace + "|" + name;
        if (TypeCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Type? found = null;
        if (xmlNamespace == Avalonia.NamespaceName)
        {
            foreach (var ns in AvaloniaNamespaces)
            {
                found = AvaloniaAssemblies.Value.Select(a => a.GetType(ns + "." + name, throwOnError: false)).FirstOrDefault(t => t is not null);
                if (found is not null)
                {
                    break;
                }
            }
        }
        else if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal))
        {
            var ns = xmlNamespace["using:".Length..];
            found = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(ns + "." + name, throwOnError: false)).FirstOrDefault(t => t is not null);
        }

        TypeCache[key] = found;
        return found;
    }

    private static void EnsureRegistered(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            RuntimeHelpers.RunClassConstructor(t.TypeHandle);
        }
    }

    private static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One binding found in the markup.</summary>
/// <param name="Mode">The text after <c>Mode=</c>, or <c>null</c> when the binding does not state one.</param>
/// <param name="TwoWayByDefault">The target property is registered with a TwoWay default binding mode.</param>
/// <param name="ViewModel">The <c>x:DataType</c> in scope, when it could be resolved.</param>
/// <param name="ElementResolved">The type that owns the property (the element, or the owner of an attached property) was found.</param>
/// <param name="Target">The view-model property the path ends in, when the path could be resolved on <paramref name="ViewModel"/>.</param>
internal sealed record BindingSite(
    string File,
    int Line,
    string Element,
    string Property,
    string Markup,
    string? Mode,
    bool TwoWayByDefault,
    Type? ViewModel,
    string? Path,
    PropertyInfo? Target,
    bool ElementResolved)
{
    public bool StatesMode => Mode is "OneWay" or "OneTime" or "TwoWay" or "OneWayToSource";

    public bool IsExplicitTwoWay => Mode == "TwoWay";

    /// <summary>True when the setter of <see cref="Target"/> is public (the only one the AOT-compiled binding may call without surprises).</summary>
    public bool HasPublicSetter => Target?.SetMethod is { IsPublic: true };

    public string Location => $"{System.IO.Path.GetRelativePath(RepoPaths.Root, File)}:{Line}: {Element}.{Property} {Markup}";
}
