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
        // StaticResource is resolved once, when the XAML loads, so it would freeze the theme of that moment.
        var offenders = AxamlFiles()
            .Where(f => !string.Equals(Path.GetFileName(f), "Tokens.axaml", StringComparison.Ordinal))
            .Where(f => StaticColourLookup().IsMatch(WithoutComments(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(RepoPaths.AppDirectory, f))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("<Setter Property=\"Foreground\" Value=\"{StaticResource Brush.Smoke}\" />", true)]
    [InlineData("Fill=\"{StaticResource Color.Ink}\"", true)]
    [InlineData("Fill=\"{StaticResource   Brush.Ink}\"", true)]
    [InlineData("Foreground=\"{DynamicResource Brush.Smoke}\"", false)]
    [InlineData("FontFamily=\"{StaticResource Font.Body}\"", false)]
    [InlineData("<StaticResource x:Key=\"TextControlBorderBrush\" ResourceKey=\"Brush.Stone\" />", false)]
    public void StaticColourLookupPattern_MatchesOnlyStaticLookupsOfColourTokens(string xaml, bool expected)
    {
        Assert.Equal(expected, StaticColourLookup().IsMatch(xaml));
    }

    private static string WithoutComments(string xaml) => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [GeneratedRegex(@"\{\s*StaticResource\s+(?:Brush|Color)\.")]
    private static partial Regex StaticColourLookup();

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
