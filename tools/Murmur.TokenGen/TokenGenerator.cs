using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Murmur.TokenGen;

/// <summary>
/// Turns <c>docs/design/tokens.json</c> into the Avalonia resource dictionary <c>Tokens.axaml</c>.
/// Reads the document with <see cref="JsonDocument"/> only (no reflection serialization), and its output is
/// fully deterministic (invariant culture, LF line endings, no BOM) so a test can compare it byte for byte.
/// </summary>
public static class TokenGenerator
{
    private const string Header = """
        <!--
          AUTO-GENERATED from docs/design/tokens.json by tools/Murmur.TokenGen - DO NOT EDIT.
          Regenerate: run the tools/Murmur.TokenGen project with the arguments docs/design/tokens.json and src/Murmur.App/Styles/Tokens.axaml

          Naming
            Color.<Name>        Color              Brush.<Name>        SolidColorBrush (one pair per colour token)
                                Both live in ResourceDictionary.ThemeDictionaries: "Light" is built from "color", "Dark" from
                                "colorDark" (same keys in both). Look them up with DynamicResource so a runtime theme change
                                re-resolves them; StaticResource would freeze the theme that was active at load time.
            Font.Display/Body   Inter (embedded via Avalonia.Fonts.Inter)    Font.Mono   Geist Mono (embedded asset)
            FontWeight.<Role>   Display = 300, Body = first body weight, BodyMedium = second body weight, Mono
            FontSize.<Scale>    double, one per typeScale entry (plus FontSize.Mono)
            LineHeight.<Scale>  double = size * ratio (Avalonia TextBlock.LineHeight is absolute, not a multiplier)
            LetterSpacing.<Scale>
                                double in DIPs = em * size, because Avalonia TextBlock.LetterSpacing is absolute.
                                display, heading and headingSm use font.display.letterSpacingEm; every other entry
                                uses font.body.letterSpacingEm. Mono uses no tracking.
            Space.<n>           double, spacing scale          Inset.<n>      uniform Thickness from the scale
            Inset.<Component>   Thickness (horizontal, vertical) pairs taken from the scale, see TokenGenerator.Insets
            Radius.<Name>       CornerRadius                   Hairline.*     border hairline width / thickness (brush: Brush.Hairline)
            Shadow.<Name>       BoxShadows (rgba converted to #AARRGGBB, px dropped; Avalonia parses unitless numbers)
            Orb.<Layer>         Color of a layer of the orb gradient (+ X, Y, Stop doubles); Orb.Base the lavender under them. Not themed.

          Everything except Color.* / Brush.* is theme-independent and sits outside the theme dictionaries.
          Accent colours are only for the recording orb and icons; Orb*.axaml and Icons/ may reference them.
        -->
        """;

    /// <summary>Typography scale entries that use the display font (light weight, negative tracking).</summary>
    private static readonly string[] DisplayScale = ["display", "heading", "headingSm", "cardTitle", "pageTitle"];

    /// <summary>Component insets: name, horizontal and vertical value. Both must exist in the spacing scale.</summary>
    private static readonly (string Name, int Horizontal, int Vertical)[] Insets =
    [
        ("Button", 24, 12),
        ("Input", 12, 8),
        ("Search", 20, 12),
        ("Card", 32, 32),
        ("SidebarFrame", 16, 20),
        ("Wordmark", 10, 4),
        ("Offline", 16, 14),
        ("TopBar", 32, 12),
        ("Key", 8, 3),
        ("Toggle", 14, 0),
        ("Suggestion", 12, 0),
        ("StartButton", 20, 0),
        ("SearchBox", 16, 0),
        ("RowPad", 20, 16),
        ("LibraryItem", 32, 0),
        ("InputBare", 2, 0),
    ];

    public static string Generate(string tokensJson)
    {
        ArgumentNullException.ThrowIfNull(tokensJson);
        using var doc = JsonDocument.Parse(tokensJson);
        var root = doc.RootElement;

        var sb = new StringBuilder(8192);
        sb.Append(Header).Append('\n');
        sb.Append("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"\n");
        sb.Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n");

        EmitThemes(sb, root.GetProperty("color"), root.GetProperty("colorDark"), root.GetProperty("border").GetProperty("hairline"));
        var font = root.GetProperty("font");
        EmitFonts(sb, font);
        EmitTypeScale(sb, font, root.GetProperty("typeScale"));
        EmitSpacing(sb, root.GetProperty("spacing"));
        EmitRadii(sb, root.GetProperty("radius"));
        EmitHairline(sb, root.GetProperty("border").GetProperty("hairline"));
        EmitShadows(sb, root.GetProperty("shadow"));
        EmitOrb(sb, root.GetProperty("orbGradient"), root.GetProperty("color"));

        sb.Append("</ResourceDictionary>\n");
        return sb.ToString();
    }

    public static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static string Num(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);

    private static void Section(StringBuilder sb, string title) => sb.Append("\n  <!-- ").Append(title).Append(" -->\n");

    /// <summary>Theme dictionary keys, in the order they are emitted, with the tokens.json section each is built from.</summary>
    private static readonly (string Variant, string Section)[] Themes = [("Light", "color"), ("Dark", "colorDark")];

    private static void EmitThemes(StringBuilder sb, JsonElement light, JsonElement dark, JsonElement hairline)
    {
        RequireSameColourKeys(light, dark);
        var hairlineColor = Pascal(hairline.GetProperty("color").GetString()!);
        if (!light.TryGetProperty(hairline.GetProperty("color").GetString()!, out _))
        {
            throw new InvalidOperationException($"border.hairline.color '{hairlineColor}' is not a colour token.");
        }

        Section(sb, "Colours and brushes, one dictionary per theme (keys are identical; the order follows 'color')");
        sb.Append("  <ResourceDictionary.ThemeDictionaries>\n");
        for (var i = 0; i < Themes.Length; i++)
        {
            var colors = i == 0 ? light : dark;
            sb.Append("    <ResourceDictionary x:Key=\"").Append(Themes[i].Variant).Append("\">\n");
            foreach (var color in light.EnumerateObject())
            {
                sb.Append("      <Color x:Key=\"Color.").Append(Pascal(color.Name)).Append("\">")
                    .Append(colors.GetProperty(color.Name).GetString()).Append("</Color>\n");
            }

            foreach (var color in light.EnumerateObject())
            {
                var name = Pascal(color.Name);
                sb.Append("      <SolidColorBrush x:Key=\"Brush.").Append(name)
                    .Append("\" Color=\"{StaticResource Color.").Append(name).Append("}\" />\n");
            }

            sb.Append("      <SolidColorBrush x:Key=\"Brush.Hairline\" Color=\"{StaticResource Color.").Append(hairlineColor).Append("}\" />\n");
            sb.Append("    </ResourceDictionary>\n");
        }

        sb.Append("  </ResourceDictionary.ThemeDictionaries>\n");
    }

    /// <summary>Both themes must define exactly the same colour keys, otherwise a lookup would fail in one of them.</summary>
    private static void RequireSameColourKeys(JsonElement light, JsonElement dark)
    {
        var lightKeys = light.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var darkKeys = dark.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missingInDark = lightKeys.Except(darkKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missingInLight = darkKeys.Except(lightKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missingInDark.Length > 0 || missingInLight.Length > 0)
        {
            throw new InvalidOperationException(
                "tokens.json: 'color' and 'colorDark' must have the same keys. "
                + $"Missing in colorDark: [{string.Join(", ", missingInDark)}]; missing in color: [{string.Join(", ", missingInLight)}].");
        }
    }

    private static string FamilyUri(string family) => family switch
    {
        "Inter" => "fonts:Inter#Inter",
        "Geist Mono" => "avares://Murmur.App/Assets/Fonts#Geist Mono",
        _ => family,
    };

    private static string WeightName(int weight) => weight switch
    {
        300 => "Light",
        400 => "Normal",
        500 => "Medium",
        600 => "SemiBold",
        700 => "Bold",
        _ => throw new InvalidOperationException($"Unsupported font weight {weight}."),
    };

    private static void EmitFonts(StringBuilder sb, JsonElement font)
    {
        Section(sb, "Fonts");
        var display = font.GetProperty("display");
        var body = font.GetProperty("body");
        var mono = font.GetProperty("mono");
        sb.Append("  <FontFamily x:Key=\"Font.Display\">").Append(FamilyUri(display.GetProperty("family").GetString()!)).Append("</FontFamily>\n");
        sb.Append("  <FontFamily x:Key=\"Font.Body\">").Append(FamilyUri(body.GetProperty("family").GetString()!)).Append("</FontFamily>\n");
        sb.Append("  <FontFamily x:Key=\"Font.Mono\">").Append(FamilyUri(mono.GetProperty("family").GetString()!)).Append("</FontFamily>\n");

        sb.Append("  <FontWeight x:Key=\"FontWeight.Display\">").Append(WeightName(display.GetProperty("weight").GetInt32())).Append("</FontWeight>\n");
        var weights = body.GetProperty("weights");
        sb.Append("  <FontWeight x:Key=\"FontWeight.Body\">").Append(WeightName(weights[0].GetInt32())).Append("</FontWeight>\n");
        sb.Append("  <FontWeight x:Key=\"FontWeight.BodyMedium\">").Append(WeightName(weights[1].GetInt32())).Append("</FontWeight>\n");
        sb.Append("  <FontWeight x:Key=\"FontWeight.Mono\">").Append(WeightName(mono.GetProperty("weight").GetInt32())).Append("</FontWeight>\n");
    }

    private static void EmitTypeScale(StringBuilder sb, JsonElement font, JsonElement scale)
    {
        Section(sb, "Type scale: size, absolute line height and absolute letter spacing (em * size)");
        var displayEm = font.GetProperty("display").GetProperty("letterSpacingEm").GetDouble();
        var bodyEm = font.GetProperty("body").GetProperty("letterSpacingEm").GetDouble();

        foreach (var entry in scale.EnumerateObject())
        {
            var name = Pascal(entry.Name);
            var size = entry.Value.GetProperty("size").GetDouble();
            var ratio = entry.Value.GetProperty("lineHeight").GetDouble();
            var em = entry.Value.TryGetProperty("letterSpacingEm", out var own)
                ? own.GetDouble()
                : Array.IndexOf(DisplayScale, entry.Name) >= 0 ? displayEm : bodyEm;

            // 'letterSpacing' is an absolute value in DIPs: the canvas sets 0.01 em once on a 14 px page and every descendant inherits 0.14 px.
            var spacing = entry.Value.TryGetProperty("letterSpacing", out var absolute) ? absolute.GetDouble() : size * em;
            sb.Append("  <x:Double x:Key=\"FontSize.").Append(name).Append("\">").Append(Num(size)).Append("</x:Double>\n");
            sb.Append("  <x:Double x:Key=\"LineHeight.").Append(name).Append("\">").Append(Num(size * ratio)).Append("</x:Double>\n");
            sb.Append("  <x:Double x:Key=\"LetterSpacing.").Append(name).Append("\">").Append(Num(spacing)).Append("</x:Double>\n");
        }

        var mono = font.GetProperty("mono");
        var monoSize = mono.GetProperty("size").GetDouble();
        sb.Append("  <x:Double x:Key=\"FontSize.Mono\">").Append(Num(monoSize)).Append("</x:Double>\n");
        sb.Append("  <x:Double x:Key=\"LineHeight.Mono\">").Append(Num(monoSize * mono.GetProperty("lineHeight").GetDouble())).Append("</x:Double>\n");
    }

    private static void EmitSpacing(StringBuilder sb, JsonElement spacing)
    {
        Section(sb, "Spacing scale");
        var values = new List<int>();
        foreach (var item in spacing.EnumerateArray())
        {
            values.Add(item.GetInt32());
        }

        foreach (var value in values)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            sb.Append("  <x:Double x:Key=\"Space.").Append(text).Append("\">").Append(text).Append("</x:Double>\n");
        }

        foreach (var value in values)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            sb.Append("  <Thickness x:Key=\"Inset.").Append(text).Append("\">").Append(text).Append("</Thickness>\n");
        }

        foreach (var (name, horizontal, vertical) in Insets)
        {
            if ((horizontal != 0 && !values.Contains(horizontal)) || (vertical != 0 && !values.Contains(vertical)))
            {
                throw new InvalidOperationException($"Inset.{name} uses a value outside the spacing scale.");
            }

            sb.Append("  <Thickness x:Key=\"Inset.").Append(name).Append("\">")
                .Append(horizontal.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(vertical.ToString(CultureInfo.InvariantCulture)).Append("</Thickness>\n");
        }
    }

    private static void EmitRadii(StringBuilder sb, JsonElement radii)
    {
        Section(sb, "Radii (CardTop / CardBottom: the first and the last row of a card of rows round only their outer corners)");
        foreach (var radius in radii.EnumerateObject())
        {
            sb.Append("  <CornerRadius x:Key=\"Radius.").Append(Pascal(radius.Name)).Append("\">")
                .Append(Num(radius.Value.GetDouble())).Append("</CornerRadius>\n");
        }

        if (radii.TryGetProperty("card", out var card))
        {
            var value = Num(card.GetDouble());
            sb.Append("  <CornerRadius x:Key=\"Radius.CardTop\">").Append(value).Append(',').Append(value).Append(",0,0</CornerRadius>\n");
            sb.Append("  <CornerRadius x:Key=\"Radius.CardBottom\">0,0,").Append(value).Append(',').Append(value).Append("</CornerRadius>\n");
        }
    }

    /// <summary>
    /// The orb as theme-independent resources: <c>Orb.Base</c>, and per layer <c>Orb.&lt;Layer&gt;</c> (colour) with <c>X</c>, <c>Y</c> (centre, as a
    /// fraction of the box) and <c>Stop</c> (where it fades out, as a fraction of the farthest-corner radius).
    /// </summary>
    private static void EmitOrb(StringBuilder sb, JsonElement orb, JsonElement colors)
    {
        Section(sb, "Orb gradient (theme-independent; first layer paints on top)");
        sb.Append("  <Color x:Key=\"Orb.Base\">").Append(OrbColour(orb.GetProperty("base").GetString()!, colors)).Append("</Color>\n");
        foreach (var layer in orb.GetProperty("layers").EnumerateArray())
        {
            var name = Pascal(layer.GetProperty("name").GetString()!);
            sb.Append("  <Color x:Key=\"Orb.").Append(name).Append("\">").Append(OrbColour(layer.GetProperty("color").GetString()!, colors)).Append("</Color>\n");
            sb.Append("  <x:Double x:Key=\"Orb.").Append(name).Append("X\">").Append(Num(layer.GetProperty("x").GetDouble())).Append("</x:Double>\n");
            sb.Append("  <x:Double x:Key=\"Orb.").Append(name).Append("Y\">").Append(Num(layer.GetProperty("y").GetDouble())).Append("</x:Double>\n");
            sb.Append("  <x:Double x:Key=\"Orb.").Append(name).Append("Stop\">").Append(Num(layer.GetProperty("stop").GetDouble())).Append("</x:Double>\n");
        }
    }

    /// <summary>A hex colour as written, or the light value of a colour token.</summary>
    private static string OrbColour(string value, JsonElement colors) =>
        value.StartsWith('#') ? value : colors.TryGetProperty(value, out var token)
            ? token.GetString()!
            : throw new InvalidOperationException($"orbGradient: '{value}' is neither a hex colour nor a colour token.");

    private static void EmitHairline(StringBuilder sb, JsonElement hairline)
    {
        Section(sb, "Hairline border (the brush, Brush.Hairline, is in the theme dictionaries)");
        var width = Num(hairline.GetProperty("width").GetDouble());
        sb.Append("  <x:Double x:Key=\"Hairline.Width\">").Append(width).Append("</x:Double>\n");
        sb.Append("  <Thickness x:Key=\"Hairline.Thickness\">").Append(width).Append("</Thickness>\n");
    }

    private static void EmitShadows(StringBuilder sb, JsonElement shadows)
    {
        Section(sb, "Shadows");
        foreach (var shadow in shadows.EnumerateObject())
        {
            sb.Append("  <BoxShadows x:Key=\"Shadow.").Append(Pascal(shadow.Name)).Append("\">")
                .Append(ConvertShadow(shadow.Value.GetString()!)).Append("</BoxShadows>\n");
        }
    }

    /// <summary>Converts a CSS box-shadow list into Avalonia's <c>x y blur spread #AARRGGBB</c> list.</summary>
    internal static string ConvertShadow(string css)
    {
        var layers = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= css.Length; i++)
        {
            if (i < css.Length)
            {
                if (css[i] == '(')
                {
                    depth++;
                }
                else if (css[i] == ')')
                {
                    depth--;
                }
            }

            if (i == css.Length || (css[i] == ',' && depth == 0))
            {
                layers.Add(ConvertShadowLayer(css[start..i].Trim()));
                start = i + 1;
            }
        }

        return string.Join(", ", layers);
    }

    private static string ConvertShadowLayer(string layer)
    {
        var rgbaAt = layer.IndexOf("rgba(", StringComparison.Ordinal);
        if (rgbaAt < 0)
        {
            throw new InvalidOperationException($"Unsupported shadow layer '{layer}': expected an rgba() colour.");
        }

        var numbers = layer[..rgbaAt].Replace("px", string.Empty, StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = layer[(rgbaAt + 5)..layer.LastIndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
        var r = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var g = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var b = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var a = (int)Math.Round(double.Parse(parts[3], CultureInfo.InvariantCulture) * 255, MidpointRounding.AwayFromZero);

        var x = numbers[0];
        var y = numbers[1];
        var blur = numbers.Length > 2 ? numbers[2] : "0";
        var spread = numbers.Length > 3 ? numbers[3] : "0";
        return string.Create(CultureInfo.InvariantCulture, $"{x} {y} {blur} {spread} #{a:X2}{r:X2}{g:X2}{b:X2}");
    }
}
