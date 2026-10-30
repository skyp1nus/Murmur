using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>One choice of the segmented Acceleration control: CPU, Vulkan or CUDA.</summary>
public sealed partial class AccelerationOptionViewModel : ObservableObject
{
    private readonly AccelerationSettingsViewModel _owner;

    internal AccelerationOptionViewModel(AiAcceleration value, string label, AccelerationSettingsViewModel owner)
    {
        Value = value;
        Label = label;
        _owner = owner;
    }

    public AiAcceleration Value { get; }

    public string Label { get; }

    /// <summary>The chosen build (<see cref="AppSettings.Acceleration"/>).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>This PC can use the build (<see cref="IAccelerationProbe.Supported"/>); an unavailable one is drawn disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    public partial bool IsAvailable { get; set; }

    /// <summary>Why an unavailable option cannot be chosen; <c>null</c> (no tooltip) while it can.</summary>
    public string? Tooltip => IsAvailable ? null : Value switch
    {
        AiAcceleration.Vulkan => Strings.Accel_UnavailableVulkan,
        AiAcceleration.Cuda => Strings.Accel_UnavailableCuda,
        _ => Strings.Accel_Unavailable,
    };

    [RelayCommand]
    private void Choose() => _owner.Choose(this);
}

/// <summary>
/// The Acceleration card of the Models section: the segmented control CPU / Vulkan / CUDA (D-042), the progress of the
/// runtime it downloads, and the reason a GPU build does not run.
/// </summary>
/// <remarks>
/// <para><b>Where the facts come from.</b> The offered builds from <see cref="IAccelerationProbe"/> (none = only the CPU
/// build), the choice from <see cref="AppSettings.Acceleration"/> through the shared <see cref="SettingsWriter"/>, the build that
/// really runs and why not the chosen one from <see cref="IAccelerationStatusSource"/> (its <c>Changed</c> event arrives on any
/// thread and is marshalled with <c>post</c>), the download of a missing runtime from the <see cref="IModelManager"/>.</para>
/// <para><b>Choosing.</b> The choice is saved first (the backend then reports "runtime not installed" and serves the CPU build
/// meanwhile); when the chosen build's runtime is not installed its download starts at once and its progress shows in the card.
/// Cancelling or a failure leaves the choice and offers the download again. Nothing downloads without a click. Only one download
/// runs: choosing another build cancels the running one first and waits for it, and a choice that a newer one has replaced
/// before its save finished downloads nothing, so the card never shows (or hides) a download that is not the chosen build's.</para>
/// <para><b>The runtime items.</b> The GPU builds are unlisted items of the model manager (<see cref="IModelManager.Catalog"/> does
/// not list them, <see cref="IModelManager.GetStatus"/> and <see cref="IModelManager.InstallAsync"/> accept their ids). Their ids
/// come from the composition root as a map (<c>AiServices.AccelerationRuntimeIds</c>); a build without an entry has nothing to download.</para>
/// </remarks>
public sealed partial class AccelerationSettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsWriter _writer;
    private readonly IModelManager? _manager;
    private readonly IAccelerationProbe? _probe;
    private readonly IAccelerationStatusSource? _source;
    private readonly IReadOnlyDictionary<AiAcceleration, string>? _runtimeIds;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private Run? _download;
    private int _choice;
    private AiAcceleration? _requested;
    private bool _downloadFailed;
    private bool _disposed;

    /// <summary>One download this view model started: its token and the moment it has fully finished (the cleanup after a cancel included).</summary>
    private sealed class Run(AiAcceleration acceleration, string id, CancellationTokenSource cts)
    {
        public AiAcceleration Acceleration { get; } = acceleration;

        public string Id { get; } = id;

        public CancellationTokenSource Cts { get; } = cts;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal AccelerationSettingsViewModel(
        SettingsWriter writer,
        IModelManager? manager,
        IAccelerationProbe? probe,
        IAccelerationStatusSource? source,
        IReadOnlyDictionary<AiAcceleration, string>? runtimeIds,
        Action<Action> post)
    {
        _writer = writer;
        _manager = manager;
        _probe = probe;
        _source = source;
        _runtimeIds = runtimeIds;
        _post = post;
        Options =
        [
            new AccelerationOptionViewModel(AiAcceleration.Cpu, Strings.Accel_Cpu, this),
            new AccelerationOptionViewModel(AiAcceleration.Vulkan, Strings.Accel_Vulkan, this),
            new AccelerationOptionViewModel(AiAcceleration.Cuda, Strings.Accel_Cuda, this),
        ];

        if (_manager is not null)
        {
            _manager.StatusChanged += OnRuntimeStatusChanged;
        }

        if (_source is not null)
        {
            _source.Changed += OnStatusChanged;
        }

        Refresh();
    }

    public IReadOnlyList<AccelerationOptionViewModel> Options { get; }

    /// <summary>The line under the control: why a GPU build does not run, that its runtime is missing, or which GPU runs it. Empty = nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(HasDetails))]
    public partial string NoteText { get; set; } = string.Empty;

    public bool HasNote => NoteText.Length > 0;

    /// <summary>Anything to show under the control (a note, the progress, the Download button or an error); the block is hidden, and takes no room, while this is false.</summary>
    public bool HasDetails => HasNote || IsDownloading || CanDownload || HasError;

    /// <summary>The runtime of the chosen build is downloading: the progress bar shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(HasDetails))]
    public partial bool IsDownloading { get; set; }

    /// <summary>The running download was started by this card, so its Cancel works.</summary>
    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    /// <summary>Download progress, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary><c>42%</c> while downloading.</summary>
    [ObservableProperty]
    public partial string PercentText { get; set; } = string.Empty;

    /// <summary><c>1.20 GB of 5.00 GB</c> while downloading.</summary>
    [ObservableProperty]
    public partial string TransferText { get; set; } = string.Empty;

    /// <summary>The chosen build is not installed and nothing downloads it now: "Download" shows (again after a cancel or a failure).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(HasDetails))]
    public partial bool NeedsDownload { get; set; }

    public bool CanDownload => NeedsDownload && !IsDownloading;

    /// <summary>The choice could not be saved, or the download failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(HasDetails))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The last choice or download started (for tests); completes when it has finished, failed or was cancelled.</summary>
    internal Task LastOperation { get; private set; } = Task.CompletedTask;

    /// <summary>The build chosen in the settings.</summary>
    public AiAcceleration Selected => _writer.Current.Acceleration;

    /// <summary>Re-reads what this PC supports, the saved choice, the status of the runtime and of the backend.</summary>
    internal void Refresh()
    {
        var supported = _probe?.Supported;
        var selected = _writer.Current.Acceleration;
        foreach (var option in Options)
        {
            option.IsAvailable = option.Value == AiAcceleration.Cpu || (supported is not null && supported.Contains(option.Value));
            option.IsSelected = option.Value == selected;
        }

        UpdateDownloadState(selected);
    }

    /// <summary>The saved settings changed elsewhere: shows the choice (no download is started by this).</summary>
    internal void ReloadSaved()
    {
        _requested = null;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_manager is not null)
        {
            _manager.StatusChanged -= OnRuntimeStatusChanged;
        }

        if (_source is not null)
        {
            _source.Changed -= OnStatusChanged;
        }

        _download?.Cts.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal void Choose(AccelerationOptionViewModel option)
    {
        // A click on the build that is chosen (or already asked for, its save still running) changes nothing.
        if (_disposed || !option.IsAvailable || option.Value == (_requested ?? _writer.Current.Acceleration))
        {
            return;
        }

        LastOperation = ChooseAsync(option.Value);
    }

    [RelayCommand]
    private void Download()
    {
        if (_disposed || IsDownloading)
        {
            return;
        }

        var selected = _writer.Current.Acceleration;
        if (RuntimeIdOf(selected) is { } id)
        {
            LastOperation = DownloadAsync(selected, id);
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cts.Cancel();

    /// <summary>The id of the runtime item of a GPU build in the model manager, or <c>null</c> (CPU, no such item on this platform, no manager).</summary>
    internal string? RuntimeIdOf(AiAcceleration acceleration) =>
        acceleration != AiAcceleration.Cpu && _runtimeIds is not null && _runtimeIds.TryGetValue(acceleration, out var id) && StatusOf(id) is not null ? id : null;

    private ModelStatus? StatusOf(string id)
    {
        try
        {
            return _manager?.GetStatus(id);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            // An item this OS does not have: nothing to download.
            return null;
        }
    }

    private async Task ChooseAsync(AiAcceleration value)
    {
        var ticket = ++_choice;
        _requested = value;

        // One download at a time: the one of the build that is being left stops, and its cleanup finishes, before anything else changes.
        if (_download is { } running)
        {
            running.Cts.Cancel();
            await running.Done.Task.ConfigureAwait(true);
        }

        if (_disposed || ticket != _choice)
        {
            return;
        }

        Error = null;
        _downloadFailed = false;
        var saveError = await _writer.UpdateAsync(s => s with { Acceleration = value }).ConfigureAwait(true);

        // A newer choice (or the end of the window) took over while the save ran: it is the one that decides what happens next.
        if (_disposed || ticket != _choice)
        {
            return;
        }

        if (saveError is not null)
        {
            Error = UiText.Format(Strings.Accel_SaveFailed, saveError);
        }

        Refresh();
        if (saveError is null
            && _writer.Current.Acceleration == value
            && RuntimeIdOf(value) is { } id
            && StatusOf(id) is { State: not ModelInstallState.Installed and not ModelInstallState.Downloading })
        {
            await DownloadAsync(value, id).ConfigureAwait(true);
        }
    }

    private async Task DownloadAsync(AiAcceleration acceleration, string id)
    {
        if (_disposed)
        {
            return;
        }

        if (IsLowOnSpace(id))
        {
            _downloadFailed = true;
            Error = Strings.Models_NoSpace;
            UpdateDownloadState(_writer.Current.Acceleration);
            return;
        }

        var run = new Run(acceleration, id, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _download = run;
        _downloadFailed = false;
        Error = null;
        IsDownloading = true;
        CanCancel = true;
        PercentText = UiText.Format(Strings.Common_Percent, 0);
        Percent = 0;
        TransferText = string.Empty;
        NoteText = UiText.Format(Strings.Accel_Downloading, LabelOf(acceleration));
        var failed = false;
        var current = false;
        try
        {
            await _manager!.InstallAsync(id, run.Cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: the partial download stays for the next try; the card offers "Download" again.
        }
        catch (Exception ex)
        {
            Trace.TraceError("Downloading the runtime '{0}' failed: {1}", id, ex);
            failed = true;
        }
        finally
        {
            // Only the download the card shows may clear it: a download that was replaced leaves the card to the new one.
            current = ReferenceEquals(_download, run);
            if (current)
            {
                _download = null;
                IsDownloading = false;
                CanCancel = false;
            }

            run.Cts.Dispose();
            run.Done.TrySetResult();
        }

        if (_disposed || !current)
        {
            return;
        }

        if (failed && _writer.Current.Acceleration == acceleration)
        {
            _downloadFailed = true;
            Error = UiText.Format(Strings.Accel_DownloadFailed, LabelOf(acceleration));
        }

        UpdateDownloadState(_writer.Current.Acceleration);
    }

    /// <summary>The drive of the models has less room than the runtime still has to download (the models list's check); unknown free space counts as enough.</summary>
    private bool IsLowOnSpace(string id)
    {
        try
        {
            var free = _manager!.AvailableFreeSpaceBytes;
            if (free is < 0 or long.MaxValue || StatusOf(id) is not { } status)
            {
                return false;
            }

            var remaining = status.Model.SizeBytes - status.BytesDone;
            return remaining > 0 && free < remaining;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnRuntimeStatusChanged(ModelStatus status)
    {
        if (_disposed || status.Model.Kind != ModelKind.LlmRuntime)
        {
            return;
        }

        _post(() =>
        {
            if (_disposed)
            {
                return;
            }

            // Only the runtime of the chosen build is the card's business; another one (a build that was left, the CPU server) is not.
            var selected = _writer.Current.Acceleration;
            if (RuntimeIdOf(selected) != status.Model.Id)
            {
                return;
            }

            if (status.State == ModelInstallState.Downloading)
            {
                ShowProgress(selected, status);
            }
            else if (_download is null)
            {
                UpdateDownloadState(selected);
            }
        });
    }

    private void OnStatusChanged(AccelerationStatus status) => _post(() =>
    {
        if (!_disposed && _download is null)
        {
            UpdateDownloadState(_writer.Current.Acceleration);
        }
    });

    private void ShowProgress(AiAcceleration acceleration, ModelStatus status)
    {
        var total = status.BytesTotal is > 0 ? status.BytesTotal.Value : status.Model.SizeBytes;
        var fraction = total > 0 ? Math.Clamp(status.BytesDone / (double)total, 0, 1) : 0;
        IsDownloading = true;
        Percent = fraction * 100;
        PercentText = UiText.Format(Strings.Common_Percent, (int)Percent);
        TransferText = UiText.Format(Strings.Models_Downloaded, UiFormat.Size(status.BytesDone), UiFormat.Size(total));
        NoteText = UiText.Format(Strings.Accel_Downloading, LabelOf(acceleration));
    }

    /// <summary>What the card says for the chosen build now: why it cannot be used, a missing runtime, a GPU that failed to start, or the GPU that runs it.</summary>
    private void UpdateDownloadState(AiAcceleration selected)
    {
        var label = LabelOf(selected);
        var option = Options.First(o => o.Value == selected);
        if (_download is { } running && running.Acceleration == selected)
        {
            return;
        }

        // A saved build this PC can no longer use (the GPU is gone): say why, offer nothing to download.
        if (!option.IsAvailable)
        {
            NeedsDownload = false;
            IsDownloading = false;
            CanCancel = false;
            NoteText = option.Tooltip ?? string.Empty;
            return;
        }

        var runtime = RuntimeIdOf(selected) is { } id ? StatusOf(id) : null;
        var missing = runtime is { State: not ModelInstallState.Installed };
        if (runtime is { State: ModelInstallState.Downloading })
        {
            // Started elsewhere: show its progress (Cancel is not ours to press).
            ShowProgress(selected, runtime);
            CanCancel = false;
            NeedsDownload = false;
            return;
        }

        IsDownloading = false;
        CanCancel = false;
        NeedsDownload = missing;

        // A report about another build than the chosen one (the answer of the backend lags the choice) says nothing about this one.
        var status = _source?.Current is { } current && current.Requested == selected ? current : null;
        if (selected == AiAcceleration.Cpu)
        {
            NoteText = string.Empty;
        }
        else if (missing)
        {
            NoteText = _downloadFailed ? string.Empty : UiText.Format(Strings.Accel_NeedsRuntime, label);
        }
        else if (status is { Fallback: AccelerationFallback.StartFailed })
        {
            NoteText = UiText.Format(Strings.Accel_GpuUnavailable, status.Detail ?? string.Empty);
        }
        else if (status is { Fallback: AccelerationFallback.RuntimeNotInstalled })
        {
            // The backend wants a runtime this manager does not know: the same words, nothing to download.
            NoteText = UiText.Format(Strings.Accel_NeedsRuntime, label);
        }
        else if (status is { Active: not AiAcceleration.Cpu } && _probe?.GpuName is { Length: > 0 } gpu)
        {
            NoteText = UiText.Format(Strings.Accel_RunningOn, gpu);
        }
        else
        {
            NoteText = string.Empty;
        }
    }

    private static string LabelOf(AiAcceleration acceleration) => acceleration switch
    {
        AiAcceleration.Vulkan => Strings.Accel_Vulkan,
        AiAcceleration.Cuda => Strings.Accel_Cuda,
        _ => Strings.Accel_Cpu,
    };
}
