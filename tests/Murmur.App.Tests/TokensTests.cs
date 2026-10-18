using System.Text;
using System.Text.RegularExpressions;
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
