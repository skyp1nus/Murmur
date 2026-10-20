using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>UI texts: Ukrainian (neutral) and English resources stay in step, nothing is hard-coded, the culture is applied.</summary>
public sealed partial class LocalizationTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk");

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static Dictionary<string, string> ReadResx(string fileName) =>
        XDocument.Load(Path.Combine(RepoPaths.ResourcesDirectory, fileName)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> Uk => ReadResx("Strings.resx");

    private static Dictionary<string, string> En => ReadResx("Strings.en.resx");

    /// <summary>Runs <paramref name="action"/> with the UI language of the current async flow switched, then restores it.</summary>
    private static T WithUiCulture<T>(CultureInfo culture, Func<T> action)
    {
        using var _ = UiCulture.Override(culture.TwoLetterISOLanguageName);
        return action();
    }

    // The resource files ----------------------------------------------------------------------------------------------

    [Fact]
    public void EveryKeyOfTheUkrainianFile_ExistsInTheEnglishFile_AndViceVersa()
    {
        var uk = Uk.Keys.ToHashSet(StringComparer.Ordinal);
        var en = En.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(uk.Except(en).Order(StringComparer.Ordinal));
        Assert.Empty(en.Except(uk).Order(StringComparer.Ordinal));
        Assert.True(uk.Count > 50, "The resource files look empty.");
    }

    [Fact]
    public void NoValueIsEmpty()
    {
        Assert.Empty(Uk.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "Strings.resx: " + p.Key));
        Assert.Empty(En.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "Strings.en.resx: " + p.Key));
    }

    [Fact]
    public void PlaceholdersMatchBetweenTheLanguages()
    {
        var uk = Uk;
        var en = En;
        var mismatches = uk.Keys.Where(en.ContainsKey)
            .Where(key => !Placeholders(uk[key]).SequenceEqual(Placeholders(en[key])))
            .Select(key => $"{key}: uk [{string.Join(",", Placeholders(uk[key]))}] en [{string.Join(",", Placeholders(en[key]))}]")
            .ToArray();

        Assert.Empty(mismatches);
        Assert.Contains(uk, p => Placeholders(p.Value).Count > 0);
    }

    [Theory]
    [InlineData("{0} із {1}", "0,1")]
    [InlineData("{1} {0}, {0}", "0,1")]
    [InlineData("{0:N1} %", "0")]
    [InlineData("без підстановок", "")]
    public void PlaceholderParser_ReturnsTheSortedDistinctIndexes(string text, string expected)
    {
        Assert.Equal(expected, string.Join(",", Placeholders(text)));
    }

    private static List<int> Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order()];

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void KeysAreValidIdentifiers()
    {
        Assert.All(Uk.Keys, key => Assert.Matches("^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$", key));
    }

    [Fact]
    public void EveryKeyIsUsedBySomeView_OrViewModel()
    {
        var used = SourceFiles(includeAxaml: true)
            .SelectMany(f => StringsReference().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(Uk.Keys.Where(key => !used.Contains(key)).Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"\bStrings\.([A-Za-z0-9_]+)")]
    private static partial Regex StringsReference();

    // Nothing hard-coded ----------------------------------------------------------------------------------------------

    /// <summary>Everything in src/Murmur.App that shows text, except the English CI diagnostics and the resources themselves.</summary>
    private static IEnumerable<string> SourceFiles(bool includeAxaml)
    {
        var skipped = new[] { "bin/", "obj/", "Diagnostics/", "Resources/Strings" };
        return Directory.EnumerateFiles(RepoPaths.AppDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || (includeAxaml && f.EndsWith(".axaml", StringComparison.Ordinal)))
            .Where(f =>
            {
                var relative = Path.GetRelativePath(RepoPaths.AppDirectory, f).Replace('\\', '/');
                return !skipped.Any(s => relative.StartsWith(s, StringComparison.Ordinal));
            });
    }

    private static string WithoutComments(string path, string text) =>
        path.EndsWith(".axaml", StringComparison.Ordinal)
            ? Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline)
            : Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

    [Fact]
    public void ViewsAndViewModels_ContainNoCyrillicLiterals()
    {
        var offenders = SourceFiles(includeAxaml: true)
            .Select(f => (File: f, Lines: WithoutComments(f, File.ReadAllText(f)).Split('\n')))
            .SelectMany(x => x.Lines.Select((line, i) => (x.File, Line: i + 1, Text: line)))
            .Where(x => Cyrillic().IsMatch(x.Text))
            .Select(x => $"{Path.GetRelativePath(RepoPaths.AppDirectory, x.File)}:{x.Line}: {x.Text.Trim()}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ViewsAndStyles_DoNotSetUserVisibleTextFromLiterals()
    {
        // Text, Content, PlaceholderText, Header, Title and ToolTip must be bindings or x:Static resources, except the brand name.
        var literal = LiteralText();
        var offenders = SourceFiles(includeAxaml: true)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal))
            .SelectMany(f => literal.Matches(WithoutComments(f, File.ReadAllText(f))).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .Where(x => !x.EndsWith("=\"Murmur\"", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"[\u0400-\u04FF]")]
    private static partial Regex Cyrillic();

    [GeneratedRegex(@"\b(?:Text|Content|PlaceholderText|Header|Title|ToolTip\.Tip|Watermark)=""(?!\{)[^""]*[A-Za-z\u0400-\u04FF][^""]*""")]
    private static partial Regex LiteralText();

    [Fact]
    public void CyrillicGuard_SeesLiteralsButNotComments()
    {
        Assert.Matches(Cyrillic(), WithoutComments("x.cs", "var a = \"Привіт\"; // ok"));
        Assert.DoesNotMatch(Cyrillic(), WithoutComments("x.cs", "var a = /* Привіт */ 1; // Привіт"));
        Assert.DoesNotMatch(Cyrillic(), WithoutComments("x.cs", "/// <summary>\"Привіт\"</summary>"));
        Assert.DoesNotMatch(Cyrillic(), WithoutComments("x.axaml", "<!-- Привіт --><TextBlock Text=\"{Binding A}\" />"));
        Assert.Matches(Cyrillic(), WithoutComments("x.axaml", "<TextBlock Text=\"Привіт\" />"));
        Assert.Matches(LiteralText(), "<Button Content=\"Back\" />");
        Assert.DoesNotMatch(LiteralText(), "<Button Content=\"{x:Static res:Strings.Common_Back}\" Command=\"{Binding BackCommand}\" />");
    }

    // The resources at run time -----------------------------------------------------------------------------------------

    [Fact]
    public void Strings_ComeFromTheNeutralUkrainianResources_ByDefault()
    {
        var uk = Uk;

        // The test runner's UI culture is invariant: the neutral resources (Ukrainian) answer.
        Assert.Equal(uk["Common_Back"], Strings.Common_Back);
        Assert.Equal("Назад", Strings.Common_Back);
        Assert.Equal(uk["Library_EmptyHint"], Strings.Library_EmptyHint);
    }

    [Fact]
    public void EveryKeyHasAProperty_ThatReturnsTheTextOfTheUiLanguage()
    {
        var uk = Uk;
        var en = En;
        var properties = typeof(Strings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        Assert.Equal(uk.Keys.Order(StringComparer.Ordinal), properties.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, property) in properties)
        {
            Assert.Equal(uk[key], WithUiCulture(Ukrainian, () => (string)property.GetValue(null)!));
            Assert.Equal(uk[key], WithUiCulture(CultureInfo.InvariantCulture, () => (string)property.GetValue(null)!));
            Assert.Equal(en[key], WithUiCulture(English, () => (string)property.GetValue(null)!));
            Assert.Equal(en[key], WithUiCulture(CultureInfo.GetCultureInfo("en-US"), () => (string)property.GetValue(null)!));
        }
    }

    [Fact]
    public void TheOsCulture_NeverChangesTheLanguage()
    {
        // The UI language comes from the settings only: whatever the OS (or the thread) culture is, the default is Ukrainian.
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var name in new[] { "en-US", "en", "de-DE", "uk-UA" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

                Assert.Equal("uk", UiCulture.Language);
                Assert.Equal(Uk["Common_Back"], Strings.Common_Back);
                Assert.Equal("42 %", UiText.Format(Strings.Common_Percent, 42));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void TheOverride_IsUndoneOnDispose()
    {
        using (UiCulture.Override("en"))
        {
            Assert.Equal("en", UiCulture.Language);
            Assert.Equal("Back", Strings.Common_Back);
            Assert.Equal("42%", UiText.Format(Strings.Common_Percent, 42));
        }

        Assert.Equal("uk", UiCulture.Language);
        Assert.Equal("Назад", Strings.Common_Back);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData("uk", "uk")]
    [InlineData("de", "uk")]
    [InlineData("", "uk")]
    [InlineData(null, "uk")]
    public void Normalize_KnowsOnlyEnglishAndUkrainian(string? language, string expected)
    {
        Assert.Equal(expected, UiCulture.Normalize(language));
    }

    [Fact]
    public void BothLanguagesAreEmbeddedInTheAppAssembly_NotInASatelliteAssembly()
    {
        // Native AOT cannot load satellite assemblies, so the English texts must live in Murmur.App.dll itself.
        var names = typeof(Strings).Assembly.GetManifestResourceNames();

        Assert.Contains("Murmur.App.Resources.Strings.resources", names);
        Assert.Contains("Murmur.App.Resources.Strings.en.resources", names);
        Assert.Equal("uk", typeof(Strings).Assembly.GetCustomAttributes(typeof(System.Resources.NeutralResourcesLanguageAttribute), false)
            .Cast<System.Resources.NeutralResourcesLanguageAttribute>().Single().CultureName);
    }

    [Fact]
    public void Get_FallsBackToTheKeyName_ForAnUnknownKey()
    {
        Assert.Equal("No_Such_Key", Strings.Get("No_Such_Key"));
        Assert.Null(Strings.Get("No_Such_Key", "en"));
    }

    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData(null, "uk")]
    [InlineData("", "uk")]
    [InlineData("de", "uk")]
    [InlineData("uk-UA", "uk")]
    public void UiCulture_Resolve_UsesEnglishOnlyForEn(string? language, string expected)
    {
        Assert.Equal(expected, UiCulture.Resolve(language).Name);
    }

    [Fact]
    public void UiCulture_Apply_StoresTheNormalizedLanguageAndTheCulture()
    {
        string? language = null;
        CultureInfo? culture = null;

        var result = UiCulture.Apply(" EN ", (l, c) => (language, culture) = (l, c));

        Assert.Equal("en", language);
        Assert.Same(result, culture);
        Assert.Equal("en", culture!.Name);
    }

    // View models in both languages ---------------------------------------------------------------------------------------

    [Fact]
    public void Dates_AreFormattedInTheUiLanguage()
    {
        var now = new TestTime(UiHarness.Now);
        var yesterday = UiHarness.Now.AddDays(-1);
        var earlier = new DateTimeOffset(2026, 3, 5, 9, 7, 0, TimeSpan.Zero);
        var lastYear = new DateTimeOffset(2025, 10, 18, 14, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            ["Сьогодні, 15:00", "Вчора, 15:00", "5 березня, 09:07", "18 жовтня 2025, 14:30"],
            WithUiCulture(Ukrainian, () => new[] { UiFormat.Date(UiHarness.Now, now), UiFormat.Date(yesterday, now), UiFormat.Date(earlier, now), UiFormat.Date(lastYear, now) }));
        Assert.Equal(
            ["Today, 15:00", "Yesterday, 15:00", "March 5, 09:07", "October 18, 2025, 14:30"],
            WithUiCulture(English, () => new[] { UiFormat.Date(UiHarness.Now, now), UiFormat.Date(yesterday, now), UiFormat.Date(earlier, now), UiFormat.Date(lastYear, now) }));
    }

    [Fact]
    public void AllTwelveMonths_HaveANameInBothLanguages()
    {
        var now = new TestTime(UiHarness.Now);
        var uk = new List<string>();
        var en = new List<string>();
        for (var month = 1; month <= 12; month++)
        {
            var date = new DateTimeOffset(2026, month, 1, 12, 0, 0, TimeSpan.Zero);
            if (date.Date == UiHarness.Now.Date || date.Date == UiHarness.Now.Date.AddDays(-1))
            {
                continue;
            }

            uk.Add(WithUiCulture(Ukrainian, () => UiFormat.Date(date, now)));
            en.Add(WithUiCulture(English, () => UiFormat.Date(date, now)));
        }

        Assert.Equal(12, uk.Count);
        Assert.Equal(12, uk.Distinct().Count());
        Assert.Equal(12, en.Distinct().Count());
        Assert.Contains("січня, 12:00", uk[0], StringComparison.Ordinal);
        Assert.Equal("December 1, 12:00", en[11]);
    }

    [Fact]
    public void Percentages_AndProcessingBadges_FollowTheUiLanguage()
    {
        var live = new SessionProcessingStatus(SessionId.New(), SessionProcessingStage.Transcribing, 0.424, null);

        Assert.Equal("42 %", WithUiCulture(Ukrainian, () => ProcessingText.PercentText(0.424)));
        Assert.Equal("42%", WithUiCulture(English, () => ProcessingText.PercentText(0.424)));
        Assert.Equal("Транскрибується 42 %", WithUiCulture(Ukrainian, () => ProcessingText.Badge(SessionState.Transcribing, live)));
        Assert.Equal("Transcribing 42%", WithUiCulture(English, () => ProcessingText.Badge(SessionState.Transcribing, live)));
        Assert.Equal("Interrupted", WithUiCulture(English, () => ProcessingText.Badge(SessionState.Interrupted, null)));
        Assert.Equal("Queued for transcription", WithUiCulture(English, () => ProcessingText.Stage(SessionProcessingStage.Queued)));
    }

    [Fact]
    public void ChatTexts_AreFormattedInTheUiLanguage()
    {
        var chat = new ChatViewModel(null, null, null, null, "/models");

        var uk = WithUiCulture(Ukrainian, () => (chat.NoticeTitle, chat.NoticeText, ChatViewModel.FailedText("boom"), chat.PlaceholderText));
        var en = WithUiCulture(English, () => (chat.NoticeTitle, chat.NoticeText, ChatViewModel.FailedText("boom"), chat.PlaceholderText));

        Assert.Equal("AI не встановлено", uk.NoticeTitle);
        Assert.Contains("файли потраплять у /models", uk.NoticeText, StringComparison.Ordinal);
        Assert.Equal("Не вдалося отримати відповідь: boom", uk.Item3);
        Assert.Equal("Запитай про будь-який запис", uk.PlaceholderText);
        Assert.Equal("AI is not installed", en.NoticeTitle);
        Assert.Contains("the files will go to /models", en.NoticeText, StringComparison.Ordinal);
        Assert.Equal("Couldn’t get an answer: boom", en.Item3);
        Assert.Equal("Ask about any recording", en.PlaceholderText);
    }

    [Fact]
    public async Task LibraryTexts_AreEnglishWhenTheUiCultureIsEnglish()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (title, hint, speaker) = WithUiCulture(English, () => (shell.Library.EmptyTitle, shell.Library.EmptyText, Strings.Segment_Me));

        Assert.Equal("No recordings yet", title);
        Assert.Equal("Click “New recording” to record your first meeting. Everything stays on your computer.", hint);
        Assert.Equal("Me", speaker);
    }

    [Fact]
    public async Task TheLibraryScreen_RendersInEnglish_WhenTheUiCultureIsEnglish()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (texts, buttons, pills, placeholder) = await Session.Dispatch(
            () => WithUiCulture(
                English,
                () =>
                {
                    var window = UiHarness.Show(shell);
                    var result = (
                        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray(),
                        window.GetVisualDescendants().OfType<Button>().Where(b => b is not Avalonia.Controls.Primitives.ToggleButton && b.IsEffectivelyVisible).Select(b => b.Content as string).ToArray(),
                        window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Select(b => b.Content as string).ToArray(),
                        window.GetVisualDescendants().OfType<TextBox>().Single().PlaceholderText);
                    window.Close();
                    return result;
                }),
            TestContext.Current.CancellationToken);

        Assert.Contains("No recordings yet", texts);
        Assert.Equal(["New recording", "New recording"], buttons);
        Assert.Equal(["Today", "Week", "All"], pills);
        Assert.Equal("Search recordings", placeholder);
    }

    [Fact]
    public async Task TheRecordingAndSessionScreens_RenderInEnglish()
    {
        using var fixture = new SessionFixture();
        fixture.UseShortTranscript();
        var harness = new UiHarness();
        harness.Store.Add(fixture.Record);
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time, action => action()),
            fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var (sessionButtons, sessionSpeakers, recordingTexts) = await Session.Dispatch(
            () => WithUiCulture(
                English,
                () =>
                {
                    var window = UiHarness.Show(shell);
                    var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToArray();
                    var speakers = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("speaker")).Select(t => t.Text).ToArray();
                    shell.ShowRecording();
                    UiHarness.Pump();
                    var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                    window.Close();
                    return (buttons, speakers, texts);
                }),
            TestContext.Current.CancellationToken);

        Assert.Contains("Back", sessionButtons);
        Assert.Contains("Play", sessionButtons);
        Assert.Contains("Transcript", sessionButtons);
        Assert.Contains("Summary", sessionButtons);
        Assert.Contains("Ask AI", sessionButtons);
        Assert.Contains("Me", sessionSpeakers);
        Assert.Contains("Others", sessionSpeakers);
        Assert.Contains("Let participants know you’re recording.", recordingTexts);
        Assert.Contains("System audio output", recordingTexts);
    }
}
