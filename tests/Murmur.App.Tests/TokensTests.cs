using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Murmur.TokenGen;

namespace Murmur.App.Tests;

/// <summary>Keeps docs/design/tokens.json the single source of truth for the UI.</summary>
public sealed partial class TokensTests
{
    [Fact]
    public void TokensAxaml_IsInSyncWithTokensJson()
    {
        var generated = TokenGenerator.Generate(File.ReadAllText(RepoPaths.TokensJson));
        var temp = Path.Combine(Path.GetTempPath(), "murmur-tokens-" + Guid.NewGuid().ToString("N") + ".axaml");
        try
        {
            File.WriteAllBytes(temp, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(generated));

            Assert.True(
                File.ReadAllBytes(temp).AsSpan().SequenceEqual(File.ReadAllBytes(RepoPaths.TokensAxaml)),
                "Tokens.axaml is stale. Run tools/Murmur.TokenGen: docs/design/tokens.json -> src/Murmur.App/Styles/Tokens.axaml.");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Generator_ProducesNamedResourcesForEveryToken()
    {
        var axaml = TokenGenerator.Generate(File.ReadAllText(RepoPaths.TokensJson));

        foreach (var key in new[]
        {
            "Color.Canvas", "Brush.Canvas", "Brush.ButtonBorder", "Brush.AccentViolet", "Brush.AccentEmber",
            "Font.Display", "Font.Body", "Font.Mono", "FontWeight.Display", "FontSize.Display", "LineHeight.Display",
            "LetterSpacing.Display", "FontSize.Caption", "FontSize.Mono", "LineHeight.Mono", "Space.4", "Space.96",
            "Radius.Input", "Radius.Small", "Radius.Card", "Radius.CardLarge", "Radius.Pill", "Shadow.Subtle",
            "Hairline.Width", "Brush.Hairline",
        })
        {
            Assert.Contains($"x:Key=\"{key}\"", axaml, StringComparison.Ordinal);
        }

        Assert.Contains("<CornerRadius x:Key=\"Radius.Pill\">9999</CornerRadius>", axaml, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"LetterSpacing.Display\">-0.96</x:Double>", axaml, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"LineHeight.Mono\">21.97</x:Double>", axaml, StringComparison.Ordinal);
        Assert.Contains("0 0 1 0 #66000000, 0 1 1 0 #0A000000, 0 2 4 0 #0A000000", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanvasValues_AreTokens_NotHandWrittenKeys()
    {
        var axaml = TokenGenerator.Generate(File.ReadAllText(RepoPaths.TokensJson));

        foreach (var key in new[]
        {
            "FontSize.Meta", "LineHeight.Small", "LetterSpacing.RowTitle", "FontSize.Wordmark", "FontSize.CardTitle", "LineHeight.MonoRow", "FontSize.MonoSmall",
            "Space.2", "Space.3", "Space.6", "Space.10", "Space.14", "Space.28", "Radius.Key", "Radius.Tile", "Radius.Note", "Radius.CardTop", "Radius.CardBottom",
            "Inset.Key", "Inset.RowPad", "Inset.TopBar", "Inset.SidebarFrame", "Orb.Base", "Orb.Cream", "Orb.Violet", "Orb.EmberX", "Orb.PinkStop",
        })
        {
            Assert.Contains($"x:Key=\"{key}\"", axaml, StringComparison.Ordinal);
        }

        Assert.Contains("<x:Double x:Key=\"LineHeight.PageTitle\">42</x:Double>", axaml, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"LetterSpacing.Wordmark\">-0.17</x:Double>", axaml, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"LetterSpacing.Meta\">0.14</x:Double>", axaml, StringComparison.Ordinal);
        Assert.Contains("<CornerRadius x:Key=\"Radius.CardTop\">20,20,0,0</CornerRadius>", axaml, StringComparison.Ordinal);
        Assert.Contains("<Color x:Key=\"Orb.Cream\">#fff1e8</Color>", axaml, StringComparison.Ordinal);
        Assert.Contains("<Color x:Key=\"Orb.Violet\">#0447ff</Color>", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void StylesAndViews_DoNotDefineKeysThatLookLikeTokens()
    {
        // D-044: the values of the canvas live in tokens.json and are generated; a style or view file only names the sizes of its own
        // components (Size.*, Margin.*, ...). The few older insets below predate the decision and move to tokens with their screens.
        var generated = new[] { "FontSize.", "LineHeight.", "LetterSpacing.", "Radius.", "Space.", "Inset.", "Orb.", "Font.", "FontWeight.", "Shadow.", "Hairline." };
        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(Path.Combine(RepoPaths.AppDirectory, "Styles"), "*.axaml")
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoPaths.AppDirectory, "Views"), "*.axaml"))
            .Where(f => !f.EndsWith("Tokens.axaml", StringComparison.Ordinal));
        foreach (var path in files)
        {
            foreach (var key in XDocument.Load(path).Descendants().Select(e => (string?)e.Attribute(Xaml + "Key")).Where(k => k is not null))
            {
                if (generated.Any(prefix => key!.StartsWith(prefix, StringComparison.Ordinal)) && !AllowedHandWritten.Contains(Path.GetFileName(path) + ": " + key))
                {
                    offenders.Add(Path.GetFileName(path) + ": " + key);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void StyleFiles_DoNotDefineTheSameKeyTwice()
    {
        // The style files share one resource scope: a key defined in two of them silently takes the value of the file included last.
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoPaths.AppDirectory, "Styles"), "*.axaml"))
        {
            var keys = XDocument.Load(path).Descendants().Select(e => (string?)e.Attribute(Xaml + "Key"))
                .Where(k => k is not null && k is not "Light" and not "Dark").Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (!owners.TryGetValue(key!, out var files))
                {
                    owners[key!] = files = [];
                }

                files.Add(Path.GetFileName(path));
            }
        }

        Assert.Empty(owners.Where(o => o.Value.Count > 1).Select(o => o.Key + ": " + string.Join(", ", o.Value)));
    }

    [Fact]
    public void StyleFiles_DoNotStyleTheSameSelectorTwice()
    {
        // Like keys, styles of every file apply app-wide: a screen's class styled in another file takes setters it never asked for.
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoPaths.AppDirectory, "Styles"), "*.axaml"))
        {
            var selectors = XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "Style")
                .Select(e => (string?)e.Attribute("Selector")).Where(s => s is not null).Distinct(StringComparer.Ordinal);
            foreach (var selector in selectors)
            {
                if (!owners.TryGetValue(selector!, out var files))
                {
                    owners[selector!] = files = [];
                }

                files.Add(Path.GetFileName(path));
            }
        }

        Assert.Empty(owners.Where(o => o.Value.Count > 1 && !SharedSelectors.Contains(o.Key)).Select(o => o.Key + ": " + string.Join(", ", o.Value)));
    }

    /// <summary>Selectors two files style on purpose, with setters that do not overlap (the shell's type on the sub-item of Controls).</summary>
    private static readonly HashSet<string> SharedSelectors = ["Button.nav.sub"];

    /// <summary>File: key of hand-written resources that look like tokens (D-044: they move into tokens.json with the screen that owns them).</summary>
    private static readonly HashSet<string> AllowedHandWritten =
    [
        "Controls.axaml: Inset.Badge",
        "Controls.axaml: Hairline.Top",
        "Controls.axaml: Hairline.End",
        "Onboarding.axaml: Inset.Onboarding",
        "Onboarding.axaml: Inset.TextButton",
        "Onboarding.axaml: Inset.Choice",
        "Onboarding.axaml: Inset.Banner",
        "Onboarding.axaml: Inset.CardLabel",
        "Onboarding.axaml: Inset.Hotkey",
        "Onboarding.axaml: Inset.HotkeyText",
        "Onboarding.axaml: Inset.Row",
        "Onboarding.axaml: Inset.Descender",
        "Onboarding.axaml: Inset.Cap",
    ];

    [Fact]
    public void OrbResources_AreOnlyReadByTheOrbAndIconFiles()
    {
        // Orb.Violet / Orb.Ember are accent colours under another name: like the accents they belong to the orb and the icons only.
        var offenders = new List<string>();
        var references = new Regex(@"(?:(?:Static|Dynamic)Resource\s+Orb\.|x:Key=""Orb\.|""Orb\.[A-Z])");
        var files = Directory.EnumerateFiles(RepoPaths.AppDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !Path.GetRelativePath(RepoPaths.AppDirectory, f).Replace('\\', '/').StartsWith("obj/", StringComparison.Ordinal)
                && !Path.GetRelativePath(RepoPaths.AppDirectory, f).Replace('\\', '/').StartsWith("bin/", StringComparison.Ordinal))
            .Where(f => !IsAccentAllowed(f) && !Path.GetFileName(f).StartsWith("Orb", StringComparison.Ordinal));
        foreach (var path in files)
        {
            if (references.IsMatch(File.ReadAllText(path)))
            {
                offenders.Add(Path.GetRelativePath(RepoPaths.AppDirectory, path));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void OrbDisc_PaintsExactlyTheLayersOfTheTokens_InTheirOrder()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.TokensJson));
        var names = document.RootElement.GetProperty("orbGradient").GetProperty("layers").EnumerateArray()
            .Select(l => TokenGenerator.Pascal(l.GetProperty("name").GetString()!))
            .ToArray();

        Assert.Equal(names, Murmur.App.Controls.OrbDisc.LayersTopFirst);
    }

    [Fact]
    public void TheGenerator_RefusesAnOrbColourThatDiffersBetweenTheThemes()
    {
        var json = File.ReadAllText(RepoPaths.TokensJson);
        var broken = json.Replace("\"accentViolet\": \"#0447ff\"\n  }", "\"accentViolet\": \"#0447fe\"\n  }", StringComparison.Ordinal);
        if (broken == json)
        {
            // The dark section has the accents last; tolerate a reordered file by changing the dark value wherever it is.
            var at = json.IndexOf("\"colorDark\"", StringComparison.Ordinal);
            broken = json[..at] + json[at..].Replace("\"accentViolet\": \"#0447ff\"", "\"accentViolet\": \"#0447fe\"", StringComparison.Ordinal);
        }

        Assert.NotEqual(json, broken);
        var error = Assert.Throws<InvalidOperationException>(() => TokenGenerator.Generate(broken));
        Assert.Contains("accentViolet", error.Message, StringComparison.Ordinal);
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement ThemeDictionary(XDocument document, string variant) =>
        document.Root!.Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == variant);

    [Fact]
    public void Generator_EmitsALightAndADarkDictionary_FromColorAndColorDark()
    {
        using var tokens = JsonDocument.Parse(File.ReadAllText(RepoPaths.TokensJson));
        var document = XDocument.Parse(TokenGenerator.Generate(File.ReadAllText(RepoPaths.TokensJson)));

        foreach (var (variant, section) in new[] { ("Light", "color"), ("Dark", "colorDark") })
        {
            var dictionary = ThemeDictionary(document, variant);
            var colours = dictionary.Elements().Where(e => e.Name.LocalName == "Color")
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value, StringComparer.Ordinal);
            var brushes = dictionary.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => (string)e.Attribute("Color")!, StringComparer.Ordinal);

            foreach (var token in tokens.RootElement.GetProperty(section).EnumerateObject())
            {
                var name = TokenGenerator.Pascal(token.Name);
                Assert.Equal(token.Value.GetString(), colours["Color." + name]);
                Assert.Equal("{StaticResource Color." + name + "}", brushes["Brush." + name]);
            }

            Assert.Equal("{StaticResource Color.Stone}", brushes["Brush.Hairline"]);
        }
    }

    [Fact]
    public void BothThemes_DefineTheSameKeys_InTheSameOrder()
    {
        using var tokens = JsonDocument.Parse(File.ReadAllText(RepoPaths.TokensJson));
        var light = tokens.RootElement.GetProperty("color").EnumerateObject().Select(p => p.Name).ToArray();
        var dark = tokens.RootElement.GetProperty("colorDark").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(light.Order(StringComparer.Ordinal), dark.Order(StringComparer.Ordinal));

        var document = XDocument.Parse(TokenGenerator.Generate(File.ReadAllText(RepoPaths.TokensJson)));
        var lightKeys = ThemeDictionary(document, "Light").Elements().Select(e => (string)e.Attribute(Xaml + "Key")!).ToArray();
        var darkKeys = ThemeDictionary(document, "Dark").Elements().Select(e => (string)e.Attribute(Xaml + "Key")!).ToArray();
        Assert.Equal(lightKeys, darkKeys);
        Assert.Equal(light.Length * 2, lightKeys.Count(k => k != "Brush.Hairline"));
        Assert.All(lightKeys, key => Assert.Matches(@"^(Color|Brush)\.[A-Za-z]+$", key));
    }

    [Fact]
    public void ThemeIndependentTokens_StayOutsideTheThemeDictionaries()
    {
        var document = XDocument.Parse(TokenGenerator.Generate(File.ReadAllText(RepoPaths.TokensJson)));
        var outside = document.Root!.Elements().Where(e => e.Name.LocalName != "ResourceDictionary.ThemeDictionaries")
            .Select(e => (string?)e.Attribute(Xaml + "Key")).Where(k => k is not null).ToHashSet();

        Assert.Contains("Font.Body", outside);
        Assert.Contains("FontSize.Body", outside);
        Assert.Contains("Space.16", outside);
        Assert.Contains("Radius.Card", outside);
        Assert.Contains("Hairline.Thickness", outside);
        Assert.Contains("Shadow.Subtle", outside);
        Assert.DoesNotContain(outside, key => key!.StartsWith("Color.", StringComparison.Ordinal) || key.StartsWith("Brush.", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_Throws_WhenTheThemesDifferInKeys()
    {
        var json = File.ReadAllText(RepoPaths.TokensJson).Replace("\"dashedBorder\": \"#45413c\",", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(RepoPaths.TokensJson), json);

        var error = Assert.Throws<InvalidOperationException>(() => TokenGenerator.Generate(json));

        Assert.Contains("dashedBorder", error.Message, StringComparison.Ordinal);
        Assert.Contains("colorDark", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_IsDeterministic()
    {
        var json = File.ReadAllText(RepoPaths.TokensJson);

        Assert.Equal(TokenGenerator.Generate(json), TokenGenerator.Generate(json));
    }

    [Fact]
    public void ViewsAndStyles_NeverUseStaticResourceForColoursOrBrushes()
    {
        // StaticResource is resolved once, when the XAML loads, so it would freeze the theme of that moment. The only
        // static colour references allowed are the ones inside a ThemeDictionaries element (they resolve per theme).
        var offenders = AxamlFiles()
            .Where(f => !string.Equals(Path.GetFileName(f), "Tokens.axaml", StringComparison.Ordinal))
            .Where(f => HasStaticColourLookup(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoPaths.AppDirectory, f))
            .ToArray();

        Assert.Empty(offenders);
    }

    private const string Root = "<Styles xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">@BODY@</Styles>";

    [Theory]
    [InlineData("<Setter Property=\"Foreground\" Value=\"{StaticResource Brush.Smoke}\" />", true)]
    [InlineData("<Rectangle Fill=\"{StaticResource Color.Ink}\" />", true)]
    [InlineData("<Rectangle Fill=\"{StaticResource   Brush.Ink}\" />", true)]
    [InlineData("<StaticResource x:Key=\"TextControlBorderBrush\" ResourceKey=\"Brush.Stone\" />", true)]
    [InlineData("<ResourceDictionary><StaticResource x:Key=\"K\" ResourceKey=\"Color.Stone\" /></ResourceDictionary>", true)]
    [InlineData("<Rectangle Fill=\"{DynamicResource Brush.Smoke}\" />", false)]
    [InlineData("<Rectangle FontFamily=\"{StaticResource Font.Body}\" />", false)]
    [InlineData("<StaticResource x:Key=\"ProgressBarCornerRadius\" ResourceKey=\"Radius.Pill\" />", false)]
    [InlineData("<!-- {StaticResource Brush.Smoke} and ResourceKey=\"Brush.Ink\" --><Rectangle />", false)]
    [InlineData("<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key=\"Dark\"><StaticResource x:Key=\"K\" ResourceKey=\"Brush.Stone\" /></ResourceDictionary></ResourceDictionary.ThemeDictionaries>", false)]
    public void StaticColourLookupCheck_FlagsOnlyStaticLookupsOfColourTokensOutsideThemeDictionaries(string body, bool expected)
    {
        Assert.Equal(expected, HasStaticColourLookup(Root.Replace("@BODY@", body, StringComparison.Ordinal)));
    }

    private static bool HasStaticColourLookup(string xaml)
    {
        var document = XDocument.Parse(xaml);
        foreach (var themed in document.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").ToList())
        {
            themed.Remove();
        }

        foreach (var comment in document.DescendantNodes().OfType<XComment>().ToList())
        {
            comment.Remove();
        }

        var text = document.ToString();
        return StaticColourLookup().IsMatch(text) || ColourResourceKey().IsMatch(text);
    }

    [GeneratedRegex(@"\{\s*StaticResource\s+(?:Brush|Color)\.")]
    private static partial Regex StaticColourLookup();

    [GeneratedRegex("ResourceKey=\"(?:Brush|Color)\\.")]
    private static partial Regex ColourResourceKey();

    [Fact]
    public void AccentColours_AreOnlyUsedByOrbAndIconFiles()
    {
        var offenders = AxamlFiles()
            .Where(f => !IsAccentAllowed(f))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("AccentViolet", StringComparison.Ordinal)
                    || text.Contains("AccentEmber", StringComparison.Ordinal)
                    || text.Contains("Color.Accent", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(RepoPaths.AppDirectory, f))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ViewsAndStyles_ContainNoLiteralHexColours()
    {
        var offenders = AxamlFiles()
            .Where(f => !string.Equals(Path.GetFileName(f), "Tokens.axaml", StringComparison.Ordinal))
            .Where(f => HexColour().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoPaths.AppDirectory, f))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AxamlScan_FindsTheShellFiles()
    {
        var names = AxamlFiles().Select(Path.GetFileName).ToArray();

        Assert.Contains("Tokens.axaml", names);
        Assert.Contains("Controls.axaml", names);
        Assert.Contains("MainWindow.axaml", names);
    }

    private static bool IsAccentAllowed(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "Tokens.axaml", StringComparison.Ordinal) || name.StartsWith("Orb", StringComparison.Ordinal))
        {
            return true;
        }

        var relative = Path.GetRelativePath(RepoPaths.AppDirectory, path).Replace('\\', '/');
        return relative.StartsWith("Icons/", StringComparison.Ordinal);
    }

    private static IEnumerable<string> AxamlFiles() =>
        Directory.EnumerateFiles(RepoPaths.AppDirectory, "*.axaml", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(RepoPaths.AppDirectory, f).Replace('\\', '/');
                return !relative.StartsWith("bin/", StringComparison.Ordinal) && !relative.StartsWith("obj/", StringComparison.Ordinal);
            });

    [GeneratedRegex(@"#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![0-9A-Za-z_])")]
    private static partial Regex HexColour();
}
