using System.Text.RegularExpressions;
using System.Xml.Linq;
using Murmur.App.Localization;
using Murmur.App.Tests.Design;

namespace Murmur.App.Tests;

/// <summary>Runs alone: it switches the process-wide UI language, as the real app does once at start (D-032).</summary>
[CollectionDefinition("ProcessWideLanguage", DisableParallelization = true)]
public sealed class ProcessWideLanguageCollection;

/// <summary>
/// Every board of the canvas (all screens, every onboarding step, both themes) rendered with the Ukrainian UI: no text that is an
/// English UI string may be left. Texts typed by the scene from the canvas's demo data (user content) are not UI texts.
/// </summary>
[Collection("ProcessWideLanguage")]
public sealed class UkrainianBoardsTests
{
    public static TheoryData<string> BoardNames => DesignBoards.Names();

    private static readonly string[] DemoContent =
    [
        DesignDemoData.RecordingTitle,
        DesignDemoData.AskLibraryPlaceholder,
    ];

    private static Dictionary<string, string> ReadResx(string file) =>
        XDocument.Load(Path.Combine(RepoPaths.ResourcesDirectory, file)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value, StringComparer.Ordinal);

    /// <summary>English UI strings that differ from their Ukrainian text (so seeing one under the Ukrainian UI is a leak).</summary>
    private static (Dictionary<string, string> Exact, List<(Regex Pattern, string Key)> Formats) EnglishOnlyTexts()
    {
        var en = ReadResx("Strings.resx");
        var uk = ReadResx("Strings.uk.resx");
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var formats = new List<(Regex, string)>();
        foreach (var (key, value) in en)
        {
            var text = value.Trim();
            if (text.Length < 4 || !uk.TryGetValue(key, out var ukText) || string.Equals(ukText.Trim(), text, StringComparison.Ordinal))
            {
                continue;
            }

            if (text.Contains('{', StringComparison.Ordinal))
            {
                var literal = Regex.Replace(text, @"\{\d+[^}]*\}", string.Empty);
                if (literal.Trim().Length >= 8)
                {
                    var pattern = "^" + Regex.Replace(Regex.Escape(text), @"\\\{\d+[^}]*\}", ".+") + "$";
                    formats.Add((new Regex(pattern, RegexOptions.CultureInvariant), key));
                }
            }
            else
            {
                exact.TryAdd(text, key);
            }
        }

        return (exact, formats);
    }

    [Theory]
    [MemberData(nameof(BoardNames))]
    public async Task EveryBoard_InUkrainian_ShowsNoEnglishUiText(string name)
    {
        var board = DesignBoards.Get(name);
        var (exact, formats) = EnglishOnlyTexts();
        string[] texts;
        UiCulture.Apply("uk");
        try
        {
            texts = await UiHarness.Session.Dispatch(
                async () =>
                {
                    using var scene = await DesignScene.BuildAsync(board, "uk");
                    return AppTexts.Collect(scene.Window).Select(t => t.Text.Trim()).ToArray();
                },
                TestContext.Current.CancellationToken);
        }
        finally
        {
            UiCulture.Apply("en");
        }

        Assert.NotEmpty(texts);
        Assert.Contains(texts, t => t.Any(c => c is >= 'А' and <= 'я' or 'і' or 'ї' or 'є' or 'ґ'));
        var leaks = new List<string>();
        foreach (var text in texts.Where(t => !DemoContent.Contains(t, StringComparer.Ordinal)))
        {
            if (exact.TryGetValue(text, out var key))
            {
                leaks.Add($"{text} ({key})");
            }
            else if (formats.FirstOrDefault(f => f.Pattern.IsMatch(text)) is { Key: { } formatKey })
            {
                leaks.Add($"{text} ({formatKey})");
            }
        }

        Assert.True(leaks.Count == 0, name + ": English text under the Ukrainian UI: " + string.Join("; ", leaks));
    }
}
