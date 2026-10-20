using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Core stores codes; the App words them in both languages (D-039) and shows legacy free text as it is.</summary>
public sealed class SessionNoteTextTests
{
    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var code in SessionNotes.KnownCodes)
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryCode_HasAnEnglishAndAUkrainianText(string code)
    {
        var key = SessionNoteText.ResourceKey(code);
        Assert.NotNull(key);

        var english = Strings.Get(key, "en");
        var ukrainian = Strings.Get(key, "uk");
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(ukrainian));
        Assert.NotEqual(english, ukrainian);
        Assert.DoesNotContain("rec.", english, StringComparison.Ordinal);
        Assert.DoesNotContain("proc.", english, StringComparison.Ordinal);
        Assert.DoesNotMatch("[А-Яа-яІіЇїЄє]", english);
        Assert.Matches("[А-Яа-яІіЇїЄє]", ukrainian);

        // The detail goes into {0} exactly when the text has a placeholder.
        var line = SessionNotes.Format(code, "DETAIL");
        Assert.Equal(english.Contains("{0}", StringComparison.Ordinal), SessionNoteText.Describe(line, "en")!.Contains("DETAIL", StringComparison.Ordinal));
        Assert.Equal(ukrainian.Contains("{0}", StringComparison.Ordinal), SessionNoteText.Describe(line, "uk")!.Contains("DETAIL", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRecorderWarningCode_IsWorded()
    {
        foreach (var code in Enum.GetValues<RecorderWarningCode>())
        {
            var warning = new RecorderWarning("screen", code, ScreenStopped: code == RecorderWarningCode.ScreenStopped);
            Assert.NotEqual(SessionNotes.CodeOf(code), SessionNoteText.Describe(warning, "en"));
            Assert.NotEqual(SessionNotes.CodeOf(code), SessionNoteText.Describe(warning, "uk"));
        }
    }

    [Fact]
    public void ACode_ShowsInTheUiLanguage()
    {
        const string stored = "rec.mic.denied\nrec.only.system";

        using (UiCulture.Override("en"))
        {
            Assert.Equal(
                "Microphone unavailable: Windows blocks access (Settings → Privacy & security → Microphone).\nRecording system audio only.",
                SessionNoteText.Describe(stored));
        }

        using (UiCulture.Override("uk"))
        {
            Assert.Equal(
                "Мікрофон недоступний: Windows забороняє доступ (Параметри → Конфіденційність і безпека → Мікрофон).\nЗаписую лише системний звук.",
                SessionNoteText.Describe(stored));
        }
    }

    [Fact]
    public void TheDetail_IsPutIntoTheText()
    {
        using (UiCulture.Override("en"))
        {
            Assert.Equal("Indexing failed: index broke", SessionNoteText.Describe("proc.index.failed|index broke"));
            Assert.Equal("System audio unavailable: No default output is available.", SessionNoteText.Describe("rec.system.unavailable|No default output is available"));
        }

        using (UiCulture.Override("uk"))
        {
            Assert.Equal("Індексування не вдалося: index broke", SessionNoteText.Describe("proc.index.failed|index broke"));
        }
    }

    [Theory]
    [InlineData("Мікрофон недоступний: Windows забороняє доступ. Записую лише системний звук.")]
    [InlineData("Writing audio failed: disk full")]
    [InlineData("rec.from.the.future|x")]
    public void LegacyFreeText_AndUnknownCodes_PassThroughUnchanged(string stored)
    {
        using (UiCulture.Override("en"))
        {
            Assert.Equal(stored, SessionNoteText.Describe(stored));
        }

        using (UiCulture.Override("uk"))
        {
            Assert.Equal(stored, SessionNoteText.Describe(stored));
        }
    }

    [Fact]
    public void CodesAndFreeText_MixInOneList_InTheirOrder()
    {
        using var en = UiCulture.Override("en");

        Assert.Equal(
            "Writing audio failed: disk full\nRecording stopped.\nScreen recording skipped frames: the computer couldn’t keep up.",
            SessionNoteText.Describe("Writing audio failed: disk full\nRecording stopped.\nrec.screen.dropped"));
    }

    [Fact]
    public void ACodeWhoseTextTakesADetail_ButHasNone_ShowsTheUnknownErrorText()
    {
        using (UiCulture.Override("en"))
        {
            Assert.Equal("Indexing failed: Unknown error", SessionNoteText.Describe("proc.index.failed"));
        }

        Assert.Equal($"Індексування не вдалося: {Strings.Get("Session_UnknownError", "uk")!.TrimEnd('.')}", SessionNoteText.Describe("proc.index.failed", "uk"));
        Assert.Equal("Microphone unavailable: Unknown error.", SessionNoteText.Describe("rec.mic.unavailable", "en"));
    }

    [Fact]
    public void TheRecoveryCode_IsWordedInBothLanguages()
    {
        Assert.Equal("The app stopped while recording and no audio could be recovered.", SessionNoteText.Describe(SessionNotes.RecoveryLost, "en"));
        Assert.Contains("відновити", SessionNoteText.Describe(SessionNotes.RecoveryLost, "uk"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingStored_GivesNothing(string? stored) => Assert.Null(SessionNoteText.Describe(stored));

    [Fact]
    public void ARecorderWarning_IsWordedLikeTheSessionNote()
    {
        using var en = UiCulture.Override("en");
        var warning = new RecorderWarning("screen", RecorderWarningCode.ScreenCpuFallback, ScreenStopped: false, CaptureFaultKind.CpuFallback);

        Assert.Equal(SessionNoteText.Describe(SessionNotes.ScreenCpuFallback), SessionNoteText.Describe(warning));
        Assert.Equal(Strings.Recording_ScreenCpuFallback, SessionNoteText.Describe(warning));
    }

    [Fact]
    public void ResxFiles_HaveTheNewKeysInBothLanguages()
    {
        foreach (var key in new[] { "Recording_DefaultTitle", "TitleMonth_01", "TitleMonth_12", "Note_MicDenied", "Note_IndexFailed" })
        {
            Assert.False(string.IsNullOrWhiteSpace(Strings.Get(key, "en")), key);
            Assert.False(string.IsNullOrWhiteSpace(Strings.Get(key, "uk")), key);
        }
    }

    [Theory]
    [InlineData("en", "Untitled — Oct 18, 01:12")]
    [InlineData("uk", "Без назви — 18 жовтня, 01:12")]
    public void TheDefaultTitle_FollowsTheCanvasInBothLanguages(string language, string expected)
    {
        using var scope = UiCulture.Override(language);
        var at = new DateTimeOffset(2026, 10, 18, 1, 12, 0, TimeSpan.Zero);

        Assert.Equal(expected, UiFormat.DefaultTitle(at, new TestTime(at)));
    }

    [Theory]
    [InlineData("en", "Untitled — Jan 5, 14:30")]
    [InlineData("uk", "Без назви — 5 січня, 14:30")]
    public void TheDefaultTitle_HasNoLeadingZeroOnTheDay(string language, string expected)
    {
        using var scope = UiCulture.Override(language);
        var at = new DateTimeOffset(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);

        Assert.Equal(expected, UiFormat.DefaultTitle(at, new TestTime(at)));
    }

    [Theory]
    [InlineData("en", "Untitled — Oct 18, 01:12")]
    [InlineData("uk", "Без назви — 18 жовтня, 01:12")]
    public async Task TheRecordingScreen_AlwaysPassesTheLocalizedTitle(string language, string expected)
    {
        using var scope = UiCulture.Override(language);
        var recorder = new FakeRecorder();
        var time = new TestTime(new DateTimeOffset(2026, 10, 18, 1, 12, 0, TimeSpan.Zero));
        var vm = new RecordingViewModel(recorder, new FakeCapture(), new FakeSessionStore(), action => action(), time: time);

        await vm.StartStopAsync();

        Assert.Equal(expected, Assert.Single(recorder.Starts).Title);
        Assert.Equal(expected, recorder.Current!.Title);
    }
}
