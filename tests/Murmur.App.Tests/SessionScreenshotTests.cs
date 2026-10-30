using Avalonia.Headless;
using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Renders the phase 2 screens with Avalonia.Headless (Skia) into artifacts/screenshots, or into the tracked docs/screenshots when MURMUR_UPDATE_SCREENSHOTS=1 (see <see cref="RepoPaths.ScreenshotsDirectory"/>).</summary>
public sealed class SessionScreenshotTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static string Path(string name) => System.IO.Path.Combine(RepoPaths.ScreenshotsDirectory, name);

    /// <summary>A deterministic speech-like envelope: 50 values per second for an hour.</summary>
    internal static byte[] FakePeaks(int seconds)
    {
        var peaks = new byte[seconds * 50];
        var noise = 12345u;
        for (var i = 0; i < peaks.Length; i++)
        {
            noise = (noise * 1103515245u) + 12345u;
            var phrase = Math.Max(0, Math.Sin(i / 410.0) * Math.Sin(i / 97.0));
            var jitter = ((noise >> 16) & 0xFF) / 255.0;
            peaks[i] = (byte)Math.Clamp(6 + (Math.Pow(phrase, 1.5) * 200) + (jitter * 30 * (0.2 + phrase)), 0, 255);
        }

        return peaks;
    }

    [Fact]
    public async Task Session_WithAOneHourTranscript_WritesPng()
    {
        using var fixture = new SessionFixture(duration: TimeSpan.FromMinutes(62));
        var transcript = TranscriptFactory.Make(fixture.Record.Id, 10_000);
        fixture.UseTranscript(transcript);
        fixture.WritePeaks(FakePeaks(62 * 60));
        var harness = new UiHarness();
        harness.Store.Add(fixture.Record);
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time, action => action()),
            fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        // Halfway through: pick a word near the middle of the hour and play from there.
        var middle = transcript.Segments[transcript.Segments.Count / 2];
        var word = middle.Words[4];
        fixture.Player.Position = word.Start + TimeSpan.FromMilliseconds(120);
        fixture.Player.State = Murmur.Core.Capture.PlaybackState.Playing;

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                shell.Session!.Tick();
                UiHarness.Pump(6);
                UiHarness.Save(window, Path("phase2-session.png"));
                window.Close();
            },
            TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(Path("phase2-session.png")).Length > 10_000, "Screenshot looks empty.");
        Assert.Equal(4, shell.Session!.Segments[transcript.Segments.Count / 2].ActiveWord);
    }

    [Fact]
    public async Task Library_WithProcessingStates_WritesPng()
    {
        using var uk = UiCulture.Override("uk");
        var harness = new UiHarness();
        var running = Sessions.Make("Щотижневий стендап команди", UiHarness.Now.AddHours(-1), TimeSpan.FromMinutes(23) + TimeSpan.FromSeconds(41), state: SessionState.Transcribing);
        var queued = Sessions.Make("Інтервʼю з кандидатом", UiHarness.Now.AddHours(-3), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(7), screen: true, state: SessionState.Recorded);
        var done = Sessions.Make("Ревʼю дизайну: нова бібліотека", UiHarness.Now.AddDays(-1).AddHours(-2), TimeSpan.FromMinutes(52) + TimeSpan.FromSeconds(9), state: SessionState.Ready);
        var failed = Sessions.Make("Дзвінок із клієнтом", UiHarness.Now.AddDays(-2), TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(30), state: SessionState.Failed);
        var interrupted = Sessions.Make("Ретроспектива спринту", UiHarness.Now.AddDays(-4), TimeSpan.FromMinutes(45), mic: false, state: SessionState.Interrupted);
        foreach (var record in new[] { running, queued, done, failed, interrupted })
        {
            harness.Store.Add(record);
        }

        var processing = new FakeProcessing();
        processing.Set(new SessionProcessingStatus(running.Id, SessionProcessingStage.Transcribing, 0.42));
        processing.Set(new SessionProcessingStatus(queued.Id, SessionProcessingStage.Queued, 0));
        processing.Set(new SessionProcessingStatus(failed.Id, SessionProcessingStage.Failed, 0, "Воркер аварійно завершився"));
        var shell = harness.CreateShell(processing: processing);
        await shell.Library.LoadAsync();

        await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("uk");
                var window = UiHarness.Show(shell);
                UiHarness.Save(window, Path("phase2-library-processing.png"));
                window.Close();
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Транскрибується 42 %", "У черзі", "Готово", "Помилка", "Перервано"],
            shell.Library.Sessions.Select(s => s.StatusText));
        Assert.True(new FileInfo(Path("phase2-library-processing.png")).Length > 10_000, "Screenshot looks empty.");
    }
}
