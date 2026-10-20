using Avalonia;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Avalonia.Headless;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.App.Tests.Fakes;

namespace Murmur.App.Tests;

/// <summary>Renders the screens with Avalonia.Headless (Skia) into docs/screenshots.</summary>
public sealed class ScreenshotTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static string Path(string name) => System.IO.Path.Combine(RepoPaths.ScreenshotsDirectory, name);

    [Fact]
    public async Task Library_WithFakeSessions_WritesPng()
    {
        var harness = new UiHarness();
        harness.SeedSessions();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Save(window, Path("phase1-library.png"));
                window.Close();
            },
            TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(Path("phase1-library.png")).Length > 10_000, "Screenshot looks empty.");
    }

    [Fact]
    public async Task RecordingIdle_WritesPng()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Save(window, Path("phase1-recording-idle.png"));
                window.Close();
            },
            TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(Path("phase1-recording-idle.png")).Length > 10_000, "Screenshot looks empty.");
    }

    [Fact]
    public async Task RecordingActive_WithFakeLevels_WritesPng()
    {
        var shell = ActiveShell();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.BreatheOrb(window.GetVisualDescendants().OfType<GlassOrbControl>().Single());
                UiHarness.Pump();
                UiHarness.Save(window, Path("phase1-recording-active.png"));
                window.Close();
            },
            TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(Path("phase1-recording-active.png")).Length > 10_000, "Screenshot looks empty.");
    }

    /// <summary>
    /// The orb's own timer runs on real time and may tick while a slow (first) window is being shown. Whatever it did
    /// before, the committed screenshot must come out the same, or every test run rewrites the tracked file. The orb itself
    /// (its blurred halo is cached by the compositor at the scale it was last drawn at) is left out: its frames are checked on
    /// a manual clock in <c>GlassOrbControlTests</c>; everything else on the page must be pixel-identical.
    /// </summary>
    [Fact]
    public async Task RecordingActive_RendersTheSameFrame_WhateverTheOrbTimerDidBefore()
    {
        var (clean, afterStrayTicks) = await Session.Dispatch(
            () => (RenderActive(strayTicks: 0), RenderActive(strayTicks: 4)),
            TestContext.Current.CancellationToken);

        Assert.Equal(clean.Pixels.Length, afterStrayTicks.Pixels.Length);
        Assert.Equal(clean.Orb, afterStrayTicks.Orb);
        var differing = 0;
        for (var i = 0; i < clean.Pixels.Length; i++)
        {
            var (x, y) = ((i / 4) % clean.Width, (i / 4) / clean.Width);
            if (clean.Pixels[i] != afterStrayTicks.Pixels[i] && !clean.Orb.Contains(new Avalonia.Point(x, y)))
            {
                differing++;
            }
        }

        Assert.True(differing == 0, $"{differing} pixel bytes outside the orb differ between two renders of the same recording screen.");
    }

    private static ShellViewModel ActiveShell()
    {
        var harness = new UiHarness();
        harness.Recorder.Elapsed = TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(34);
        harness.Recorder.Levels = new AudioLevels(0.45f, 0.7f);
        harness.Recorder.ForceRecording(Sessions.Make("Live", UiHarness.Now, TimeSpan.Zero, state: SessionState.Recording));
        var shell = harness.CreateShell();
        shell.ShowRecording();
        shell.Recording.Refresh();
        return shell;
    }

    /// <summary>
    /// Renders the active recording screen. <paramref name="strayTicks"/> ticks of the orb's real-time timer come first, at
    /// uneven moments, like the ones that can run while a slow first window is being shown.
    /// </summary>
    private static (byte[] Pixels, int Width, Avalonia.Rect Orb) RenderActive(int strayTicks)
    {
        var shell = ActiveShell();
        var window = UiHarness.Show(shell);
        var orb = window.GetVisualDescendants().OfType<GlassOrbControl>().Single();
        for (var i = 0; i < strayTicks; i++)
        {
            Thread.Sleep(70 + (i * 13));
            orb.Tick();
        }

        if (strayTicks > 0)
        {
            Assert.True(orb.AppliedFrames > 1, "The stray ticks should have started the animation.");
        }

        UiHarness.BreatheOrb(orb);

        // The page polls the levels on its own timer, and every poll moves the level strips on by one sample: fill them with the
        // levels the recorder reports (ActiveShell), so a poll that ran (or not) while the window was shown leaves the frame as it was.
        foreach (var (track, level) in new[] { (shell.Recording.MicrophoneTrack, 0.45f), (shell.Recording.SystemTrack, 0.7f) })
        {
            for (var i = 0; i < Murmur.App.Controls.LevelHistory.PastCount; i++)
            {
                track.History.Push(level);
            }
        }

        UiHarness.Pump();
        var pixels = UiHarness.Pixels(window);
        var origin = orb.TranslatePoint(default, window) ?? default;
        var area = new Avalonia.Rect(origin, orb.Bounds.Size).Inflate(400); // the blurred halo reaches far beyond the control
        var width = (int)window.Bounds.Width;
        window.Close();
        return (pixels, width, area);
    }
}
