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
    /// before, the committed screenshot must come out the same, or every test run rewrites the tracked file. Nothing is left out:
    /// every pixel of the page must be identical.
    /// </summary>
    [Fact]
    public async Task RecordingActive_RendersTheSameFrame_WhateverTheOrbTimerDidBefore()
    {
        var (clean, afterStrayTicks) = await Session.Dispatch(
            () => (RenderActive(strayTicks: 0), RenderActive(strayTicks: 4)),
            TestContext.Current.CancellationToken);

        Assert.Equal(clean.Length, afterStrayTicks.Length);
        var differing = 0;
        for (var i = 0; i < clean.Length; i++)
        {
            if (clean[i] != afterStrayTicks[i])
            {
                differing++;
            }
        }

        Assert.True(differing == 0, $"{differing} pixel bytes differ between two renders of the same recording screen.");
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

        // The default title carries the wall-clock time ("Untitled — Oct 30, 16:37"): two renders a minute apart would differ in its glyphs.
        shell.Recording.Title = "Weekly sync";
        return shell;
    }

    /// <summary>
    /// Renders the active recording screen. <paramref name="strayTicks"/> ticks of the orb's real-time timer come first, at
    /// uneven moments, like the ones that can run while a slow first window is being shown.
    /// </summary>
    private static byte[] RenderActive(int strayTicks)
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

        // The compositor caches the blurred halo at the scale it was last drawn at; whatever the stray ticks left behind is
        // thrown away by drawing both layers of the orb afresh.
        foreach (var layer in orb.Children)
        {
            layer.InvalidateVisual();
        }

        // The page polls the levels on its own timer, and every poll moves the level strips on by one sample: fill them with the
        // levels the recorder reports (ActiveShell), so a poll that ran (or not) while the window was shown leaves the frame as it was.
        foreach (var (track, level) in new[] { (shell.Recording.MicrophoneTrack, 0.45f), (shell.Recording.SystemTrack, 0.7f) })
        {
            for (var i = 0; i < Murmur.App.Controls.LevelHistory.PastCount; i++)
            {
                track.History.Push(level);
            }
        }

        UiHarness.Pump(8);
        var pixels = UiHarness.Pixels(window);
        window.Close();
        return pixels;
    }
}
