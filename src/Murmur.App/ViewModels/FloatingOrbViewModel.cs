using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Platform;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>What the floating orb's buttons do in the app; the composition glue (<c>FloatingOrbController</c>) supplies them.</summary>
/// <param name="PauseResume">Pauses the running recording or resumes the paused one (the Recording screen's pause button).</param>
/// <param name="Stop">Stops and transcribes (the Recording screen's stop button).</param>
/// <param name="Open">Brings the main window to the front on the Recording screen.</param>
public sealed record FloatingOrbActions(Func<Task> PauseResume, Func<Task> Stop, Action Open);

/// <summary>
/// The floating recorder orb (#46): a small always-on-top orb that shows a recording is running while the main window is hidden,
/// minimised or on another page, and opens a pill with Mark, Pause/Resume, Stop and Open.
/// </summary>
/// <remarks>
/// <para><b>Visible</b> only while the recorder is Recording or Paused and the main window does not show the Recording screen
/// (<see cref="IsRecorderInView"/>, set by the glue); hidden when idle. Starting and Stopping count as not running.</para>
/// <para><b>Motion.</b> The orb turns (9 s a turn) and pings (every 3 s) only while recording, visible and with motion allowed;
/// paused it is grey and still; under the system's reduce-motion preference the state is shown by colour only.</para>
/// <para><b>Pointer.</b> The view reports the press, the movement since the press and the release. A release after less than
/// <see cref="DragThreshold"/> DIPs of movement is a click and toggles the pill; a longer move is a drag, which moves the window
/// (the view's job) and ends with the new position saved by <see cref="SavePositionAsync"/>.</para>
/// <para>Recorder, marks and motion events arrive on any thread; they are marshalled to the UI thread through <c>post</c>.</para>
/// </remarks>
public sealed partial class FloatingOrbViewModel : ObservableObject, IDisposable
{
    /// <summary>The movement, in DIPs, from which a press on the orb is a drag and no longer a click.</summary>
    public const double DragThreshold = 4;

    /// <summary>How long the chip "Marked 12:41" stays.</summary>
    internal static readonly TimeSpan ChipFor = TimeSpan.FromSeconds(2);

    private readonly IRecorder _recorder;
    private readonly FloatingOrbActions _actions;
    private readonly IRecordingMarks? _marks;
    private readonly ISettingsStore? _settings;
    private readonly IMotionPreference? _motion;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _chipReset;
    private bool _pressed;
    private bool _dragging;
    private int _disposed;

    /// <param name="recorder">The recording whose state the orb shows.</param>
    /// <param name="actions">What the buttons do.</param>
    /// <param name="marks">The Mark button and the chip; <c>null</c> = the Mark button is disabled.</param>
    /// <param name="settings">Where the position of the orb is kept; <c>null</c> = it is not remembered.</param>
    /// <param name="motion">The system's reduce-motion preference; <c>null</c> = motion is never reduced.</param>
    /// <param name="time">The clock of the chip's 2 s; the system clock when <c>null</c>.</param>
    /// <param name="post">Marshals a callback to the UI thread; the UI dispatcher when <c>null</c>.</param>
    public FloatingOrbViewModel(
        IRecorder recorder,
        FloatingOrbActions actions,
        IRecordingMarks? marks = null,
        ISettingsStore? settings = null,
        IMotionPreference? motion = null,
        TimeProvider? time = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(actions);
        _recorder = recorder;
        _actions = actions;
        _marks = marks;
        _settings = settings;
        _motion = motion;
        _time = time ?? TimeProvider.System;
        _post = post ?? (static action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        // Subscribe first, read after: a change in between is not lost.
        recorder.StateChanged += OnRecorderStateChanged;
        State = recorder.State;
        if (marks is not null)
        {
            marks.Marked += OnMarked;
        }

        if (motion is not null)
        {
            motion.Changed += OnMotionChanged;
            ReduceMotion = motion.ReduceMotion;
        }
    }

    /// <summary>The recorder's state as last seen on the UI thread.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsRecording), nameof(IsPaused), nameof(IsOrbVisible), nameof(IsSpinning), nameof(PauseLabel), nameof(StateLabel))]
    [NotifyCanExecuteChangedFor(nameof(MarkCommand), nameof(PauseResumeCommand), nameof(StopCommand))]
    public partial RecorderState State { get; set; }

    /// <summary>The main window is showing the Recording screen (visible, not minimised): the orb is not needed then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrbVisible), nameof(IsSpinning))]
    public partial bool IsRecorderInView { get; set; }

    /// <summary>The system asks for less motion: no rotation and no ping, the state is shown by colour only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpinning))]
    public partial bool ReduceMotion { get; set; }

    /// <summary>The pill with the buttons is open (to the left of the orb).</summary>
    [ObservableProperty]
    public partial bool IsPillOpen { get; set; }

    /// <summary>The chip "Marked 12:41" is on screen (for <see cref="ChipFor"/> after a mark).</summary>
    [ObservableProperty]
    public partial bool IsChipShown { get; set; }

    /// <summary>The time of the latest mark: <c>12:41</c>, or <c>1:02:03</c> from one hour on (Geist Mono).</summary>
    [ObservableProperty]
    public partial string ChipTime { get; set; } = string.Empty;

    /// <summary>"Marked", the word before <see cref="ChipTime"/>.</summary>
    public string ChipLabel => Strings.Recording_Marked;

    /// <summary>Recording or paused: a session is open.</summary>
    public bool IsRunning => State is RecorderState.Recording or RecorderState.Paused;

    public bool IsRecording => State == RecorderState.Recording;

    /// <summary>Paused: the orb is grey and still.</summary>
    public bool IsPaused => State == RecorderState.Paused;

    /// <summary>The orb window is on screen: a recording runs (or is paused) and the main window is not on the Recording screen.</summary>
    public bool IsOrbVisible => IsRunning && !IsRecorderInView;

    /// <summary>The orb turns and pings: recording, on screen, with motion allowed. Under reduce-motion it is still, whatever the state.</summary>
    public bool IsSpinning => IsRecording && IsOrbVisible && !ReduceMotion;

    /// <summary>"Pause", or "Resume" while paused.</summary>
    public string PauseLabel => IsPaused ? Strings.Recording_Resume : Strings.Recording_Pause;

    /// <summary>The name of the orb for screen readers and its tooltip.</summary>
    public string StateLabel => IsPaused ? Strings.FloatingOrb_Paused : Strings.FloatingOrb_Recording;

    public string MarkLabel => Strings.Hotkeys_Mark;

    public string StopLabel => Strings.Recording_StopAndTranscribe;

    public string OpenLabel => Strings.FloatingOrb_Open;

    /// <summary>Marks the current moment of the recording; the chip confirms it. Works while recording or paused.</summary>
    [RelayCommand(CanExecute = nameof(CanMark))]
    private void Mark() => _marks?.TryMark(out _);

    private bool CanMark() => _marks is not null && IsRunning;

    /// <summary>Pauses the recording, or resumes it while paused.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task PauseResumeAsync() => _actions.PauseResume();

    /// <summary>Stops and transcribes. The pill closes.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        IsPillOpen = false;
        await _actions.Stop();
    }

    /// <summary>Brings the main window to the Recording screen. The pill closes.</summary>
    [RelayCommand]
    private void Open()
    {
        IsPillOpen = false;
        _actions.Open();
    }

    // ---- the pointer ---------------------------------------------------------------------------------------------------------

    /// <summary>The primary button went down on the orb.</summary>
    public void PointerPressed()
    {
        _pressed = true;
        _dragging = false;
    }

    /// <summary>The pointer moved while pressed; the offset is the whole distance since the press, in DIPs. Returns true while it is a drag.</summary>
    public bool PointerMoved(double offsetX, double offsetY)
    {
        if (!_pressed)
        {
            return false;
        }

        if (!_dragging && (offsetX * offsetX) + (offsetY * offsetY) >= DragThreshold * DragThreshold)
        {
            _dragging = true;
        }

        return _dragging;
    }

    /// <summary>
    /// The button went up. A click (no drag) toggles the pill; a drag does not. Returns true after a drag, when the new position is
    /// to be saved.
    /// </summary>
    public bool PointerReleased()
    {
        if (!_pressed)
        {
            return false;
        }

        var wasDrag = _dragging;
        _pressed = false;
        _dragging = false;
        if (!wasDrag)
        {
            IsPillOpen = !IsPillOpen;
        }

        return wasDrag;
    }

    /// <summary>The press ended without a release (the pointer capture was lost): neither a click nor a finished drag.</summary>
    public void PointerCancelled()
    {
        _pressed = false;
        _dragging = false;
    }

    // ---- the position --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Where the orb goes on the given screens (the first is the primary one): the remembered spot with the whole orb inside a work area,
    /// the default corner when nothing is remembered.
    /// </summary>
    /// <param name="areas">The screens' work areas, the primary one first.</param>
    /// <param name="current">Where the orb was last put in this run, as a position; it wins over the settings (a save may still be pending or have failed).</param>
    /// <param name="primaryKnown">False when the platform names no primary screen: the scale of the saved numbers is unknown, so nothing saved is used.</param>
    public OrbPlacement? ResolvePlacement(IReadOnlyList<WorkArea> areas, FloatingOrbPosition? current = null, bool primaryKnown = true) =>
        FloatingOrbPlacement.Resolve(primaryKnown ? current ?? _settings?.Current.FloatingOrbPosition : null, areas);

    /// <summary>Saves where the orb was left (top-left in pixels, turned into DIPs of the screen it is on). Resolves to the inline error text, or <c>null</c>.</summary>
    public Task<string?> SavePositionAsync(int x, int y, IReadOnlyList<WorkArea> areas)
    {
        if (_settings is null || FloatingOrbPlacement.ToSaved(x, y, areas) is not { } position)
        {
            return Task.FromResult<string?>(null);
        }

        return SettingsWriter.For(_settings).UpdateAsync(settings => settings with { FloatingOrbPosition = position });
    }

    // ---- events from other threads -------------------------------------------------------------------------------------------

    private void OnRecorderStateChanged(RecorderState state) => _post(() =>
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        State = state;
        if (!IsRunning)
        {
            // The recording is over: the pill and the chip do not wait for the next one.
            IsPillOpen = false;
            HideChip();
        }
    });

    private void OnMotionChanged() => _post(() =>
    {
        if (Volatile.Read(ref _disposed) == 0 && _motion is not null)
        {
            ReduceMotion = _motion.ReduceMotion;
        }
    });

    private void OnMarked(TimeSpan at) => _post(() =>
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ShowChip(at);
        }
    });

    private void ShowChip(TimeSpan at)
    {
        ChipTime = UiFormat.Clock(at);
        StartChipTimer();
    }

    private void StartChipTimer()
    {
        IsChipShown = true;
        var reset = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _chipReset, reset);
        previous?.Cancel();
        previous?.Dispose();
        _ = HideChipLaterAsync(reset.Token);
    }

    private async Task HideChipLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ChipFor, _time, token);
            _post(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    IsChipShown = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Marked again: the newer timer owns the chip.
        }
    }

    private void HideChip()
    {
        var previous = Interlocked.Exchange(ref _chipReset, null);
        previous?.Cancel();
        previous?.Dispose();
        IsChipShown = false;
    }

    partial void OnIsRecorderInViewChanged(bool value)
    {
        if (value)
        {
            // The Recording screen is back in view: the pill has nothing to add and does not wait for the next time.
            IsPillOpen = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _recorder.StateChanged -= OnRecorderStateChanged;
        if (_marks is not null)
        {
            _marks.Marked -= OnMarked;
        }

        if (_motion is not null)
        {
            _motion.Changed -= OnMotionChanged;
        }

        var previous = Interlocked.Exchange(ref _chipReset, null);
        previous?.Cancel();
        previous?.Dispose();
    }
}
