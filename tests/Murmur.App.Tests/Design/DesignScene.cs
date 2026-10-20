using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Capture;
using Murmur.Core.Intelligence;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Design;

/// <summary>An artboard put on screen: the window at the size of the reference, in the theme, in the state of the board.</summary>
internal sealed class DesignScene : IDisposable
{
    private readonly DesignRig _rig;
    private readonly IDisposable _culture;

    private DesignScene(DesignBoard board, DesignRig rig, MainWindow window, IDisposable culture)
    {
        Board = board;
        _rig = rig;
        Window = window;
        _culture = culture;
    }

    public DesignBoard Board { get; }

    public MainWindow Window { get; }

    /// <summary>Things that did not come out as the board describes (the state could not be reached); they go to the report.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>
    /// Builds the board. Must run on the UI thread of the headless session. The UI language is English for the whole life of
    /// the scene (the scope is undone by <see cref="Dispose"/>).
    /// </summary>
    public static async Task<DesignScene> BuildAsync(DesignBoard board)
    {
        var culture = UiCulture.Override("en");
        var rig = new DesignRig(board.IsOnboarding);
        var notes = new List<string>();
        MainWindow window;
        if (board.IsOnboarding)
        {
            window = CreateWindow(board, rig);
            window.Show();
            UiHarness.Pump(6);
            await DriveOnboardingAsync(board, rig, window, notes);
        }
        else
        {
            await PrepareMainScreenAsync(board, rig, notes);
            window = CreateWindow(board, rig);
            window.Show();
        }

        var scene = new DesignScene(board, rig, window, culture);
        scene.Notes.AddRange(notes);
        scene.Settle();
        return scene;
    }

    /// <summary>The window at the size of the reference and in the theme, before its first frame; scale 1.</summary>
    private static MainWindow CreateWindow(DesignBoard board, DesignRig rig) =>
        new()
        {
            DataContext = rig.Shell,
            Width = board.Width,
            Height = board.Height,
            MinWidth = 0,
            MinHeight = 0,
            CanResize = false,
            RequestedThemeVariant = board.Dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };

    /// <summary>Runs the pending work, lets transitions finish, freezes the orbs and takes the focus away (no caret, no focus ring).</summary>
    public void Settle()
    {
        UiHarness.Pump(4);
        Thread.Sleep(600);
        UiHarness.Pump(4);
        foreach (var orb in Window.GetVisualDescendants().OfType<OrbControl>().ToArray())
        {
            // One frame at rest: a manual clock that never moves, so every run draws the same orb.
            orb.Clock = new TestTime(UiHarness.Now);
            orb.Tick();
        }

        // A live meter reads a constant level, so the timer ticks that run while the frame is taken do not change the bars.
        foreach (var meter in Window.GetVisualDescendants().OfType<LevelMeterControl>().ToArray())
        {
            if (meter.IsActive)
            {
                meter.LevelSource = ConstantLevel.Instance;
                for (var i = 0; i < LevelMeterControl.BarCount; i++)
                {
                    meter.Tick();
                }
            }
        }

        // A scroll area taller than its window shows a thumb the canvas does not have: say how much is cut off, then hide it.
        foreach (var scroll in Window.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible).ToArray())
        {
            if (scroll.Extent.Height > scroll.Viewport.Height + 1)
            {
                Notes.Add($"A scroll area holds {scroll.Extent.Height:0} px of content in {scroll.Viewport.Height:0} px: the rest is not on the board.");
            }

            scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        }

        // Nothing keeps the focus: no caret in a text box, no focus ring on a button.
        if (Window.FocusManager?.GetFocusedElement() is Control focused && focused is not Avalonia.Controls.Window)
        {
            Window.Focus(NavigationMethod.Unspecified);
            UiHarness.Pump(2);
            if (ReferenceEquals(Window.FocusManager?.GetFocusedElement(), focused))
            {
                Notes.Add("The focus stayed on " + focused.GetType().Name + "; a caret or focus ring may show.");
            }
        }

        // Settled means two frames in a row are the same: a transition that is still running (a button changing its colour)
        // would make the render depend on how busy the machine is.
        byte[]? previous = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            UiHarness.Pump(3);
            var frame = Render().Rgba;
            if (previous is not null && frame.AsSpan().SequenceEqual(previous))
            {
                return;
            }

            previous = frame;
            Thread.Sleep(100);
        }

        Notes.Add("The frame did not settle: it kept changing for 3 s.");
    }

    /// <summary>The rendered frame at the size of the window.</summary>
    public PixelImage Render()
    {
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window produced no frame.");
        using var buffer = frame.Lock();
        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), pixels, y * width * 4, width * 4);
        }

        return PixelImage.FromFrame(pixels, width, height, buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888);
    }

    public void Dispose()
    {
        Window.Close();
        _rig.Dispose();
        _culture.Dispose();
    }

    private sealed class ConstantLevel : ILevelSource
    {
        public static readonly ConstantLevel Instance = new();

        public float Level => 0.45f;
    }

    // ---- the main screens -----------------------------------------------------------------------------------------------

    private static async Task PrepareMainScreenAsync(DesignBoard board, DesignRig rig, List<string> notes)
    {
        var shell = rig.Shell;
        await rig.Library.LoadAsync();
        switch (board.Screen)
        {
            case BoardScreen.Library:
                break;

            case BoardScreen.Recording:
                await StartRecordingAsync(rig, notes);
                break;

            case BoardScreen.Session:
                shell.OpenSession(rig.ClientDemo);
                await shell.SessionLoadTask;
                PositionSession(rig, notes);
                break;

            case BoardScreen.Chat:
                await AskAsync(rig, notes);
                break;

            case BoardScreen.Settings:
                PrepareSettings(rig);
                shell.ShowSettings();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(board), board.Screen, "Not a main screen.");
        }
    }

    private static async Task StartRecordingAsync(DesignRig rig, List<string> notes)
    {
        rig.Shell.ShowRecording();
        rig.Recording.Microphone = true;
        rig.Recording.SystemAudio = true;
        rig.Recording.Screen = false;
        await rig.Recording.StartStopAsync();
        rig.Recorder.Elapsed = DesignDemoData.RecordingElapsed;
        rig.Recorder.Levels = new AudioLevels(DesignDemoData.RecordingLevels.Microphone, DesignDemoData.RecordingLevels.SystemAudio);
        rig.Recording.Refresh();
        if (rig.Recording.State != RecorderState.Recording)
        {
            notes.Add("The recording did not start: state " + rig.Recording.State);
        }
    }

    /// <summary>The 12:21 segment is current (the player is paused there). The search hit "timestamps" has no place in the app yet.</summary>
    private static void PositionSession(DesignRig rig, List<string> notes)
    {
        var session = rig.Shell.Session!;
        session.SeekTo(DesignDemoData.SessionPosition);
        session.Tick();
        if (session.CurrentSegmentIndex != 2)
        {
            notes.Add("The current segment is " + session.CurrentSegmentIndex + ", the board has the 12:21 one (index 2).");
        }
    }

    /// <summary>Both exchanges of the canvas, answered (the answer streams in one go, the chips come with it).</summary>
    private static async Task AskAsync(DesignRig rig, List<string> notes)
    {
        rig.Shell.ShowLibraryChat();
        var chat = rig.Shell.LibraryChat!.Chat;
        foreach (var exchange in DesignDemoData.Chat)
        {
            chat.Question = exchange.Question;
            chat.AskCommand.Execute(null);
            var citations = exchange.Sources.Select(s => new Citation(
                s.Recording == "Sprint retro" ? rig.SprintRetro.Id : rig.ClientDemo.Id,
                TimeSpan.FromSeconds(s.AtSeconds),
                s.Label,
                s.Quote)).ToArray();
            rig.Assistant.Answer([exchange.Answer], citations);
            await PumpUntilAsync(chat.AskTask);
        }

        if (chat.Messages.Count != 2 * DesignDemoData.Chat.Count)
        {
            notes.Add("The chat has " + chat.Messages.Count + " messages, the board has " + (2 * DesignDemoData.Chat.Count) + ".");
        }
    }

    /// <summary>Models of the canvas: three installed, Gemma 4 E4B at 42 %, the 26B one not installed.</summary>
    private static void PrepareSettings(DesignRig rig)
    {
        rig.Manager.SetInstalled(DesignDemoData.Ids.Asr);
        rig.Manager.SetInstalled(DesignDemoData.Ids.Vad);
        rig.Manager.SetInstalled(DesignDemoData.Ids.Embedding);
        var models = rig.Settings.Models!;
        models.Refresh();
        var small = models.Rows.Single(r => r.Id == DesignDemoData.Ids.ChatSmall);
        small.InstallCommand.Execute(null);
        rig.Manager.Progress(DesignDemoData.Ids.ChatSmall, 5_000_000_000L * DesignDemoData.DownloadingPercent / 100, 5_000_000_000);
    }

    // ---- the onboarding -------------------------------------------------------------------------------------------------

    private static async Task DriveOnboardingAsync(DesignBoard board, DesignRig rig, MainWindow window, List<string> notes)
    {
        switch (board.Screen)
        {
            case BoardScreen.Onboarding1Welcome:
                break;

            case BoardScreen.Onboarding2Audio:
                // The microphone is refused (the "allow" card, Continue disabled); the other side of the call is metered.
                rig.Devices.CreateFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("Access denied");
                await rig.GoToStepAsync(2);
                await rig.Onboarding.Audio.Settled;
                break;

            case BoardScreen.Onboarding3Models:
                await rig.GoToStepAsync(3);
                await rig.Onboarding.Audio.Settled;
                rig.Manager.Progress(DesignDemoData.Ids.Asr, DesignDemoData.OnboardingParakeetDone, 670_000_000);
                break;

            case BoardScreen.Onboarding3ModelsReady:
                await rig.GoToStepAsync(3);
                await CompleteDownloadsAsync(rig);
                break;

            case BoardScreen.Onboarding4Preferences:
                await rig.GoToStepAsync(3);
                await CompleteDownloadsAsync(rig);
                await rig.GoToStepAsync(4);
                break;

            case BoardScreen.Onboarding5FirstRecording:
                await rig.GoToStepAsync(3);
                await CompleteDownloadsAsync(rig);
                await rig.GoToStepAsync(5);
                await DesignRig.UntilAsync(() => rig.Onboarding.FirstRecording.State == TrialState.Recording, "the trial recording");
                rig.Recorder.Levels = new AudioLevels(0.4f, 0.1f);
                await rig.RunClockUntilAsync(() => rig.Onboarding.FirstRecording.ClockText == DesignDemoData.OnboardingClock);
                if (rig.Onboarding.FirstRecording.ClockText != DesignDemoData.OnboardingClock)
                {
                    notes.Add("The listening clock shows " + rig.Onboarding.FirstRecording.ClockText + ", the board has " + DesignDemoData.OnboardingClock + ".");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(board), board.Screen, "Not an onboarding step.");
        }

        if (board.Screen != BoardScreen.Onboarding1Welcome && rig.Onboarding.StepNumber != StepOf(board.Screen))
        {
            notes.Add("The onboarding is on step " + rig.Onboarding.StepNumber + ", the board has step " + StepOf(board.Screen) + ".");
        }
    }

    private static int StepOf(BoardScreen screen) => screen switch
    {
        BoardScreen.Onboarding1Welcome => 1,
        BoardScreen.Onboarding2Audio => 2,
        BoardScreen.Onboarding3Models or BoardScreen.Onboarding3ModelsReady => 3,
        BoardScreen.Onboarding4Preferences => 4,
        _ => 5,
    };

    /// <summary>Chooses Gemma 4 E4B and finishes every download (the catalog has no separate runtime).</summary>
    private static async Task CompleteDownloadsAsync(DesignRig rig)
    {
        rig.Onboarding.Models.Choices[0].SelectCommand.Execute(null);
        rig.Manager.Complete(FakeModelManager.AsrId);
        rig.Manager.Complete(FakeModelManager.VadId);
        await DesignRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.EmbeddingId), "the embedding install");
        rig.Manager.Complete(FakeModelManager.EmbeddingId);
        await DesignRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId), "the chat model install");
        rig.Manager.Complete(FakeModelManager.ChatSmallId);
        await DesignRig.UntilAsync(() => rig.Onboarding.Models.AllDone, "all downloads done");
    }

    /// <summary>Runs UI jobs until <paramref name="task"/> completes (its continuations are posted to the dispatcher).</summary>
    private static async Task PumpUntilAsync(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for the chat answer.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(2);
        }

        await task;
        UiHarness.Pump();
    }
}
