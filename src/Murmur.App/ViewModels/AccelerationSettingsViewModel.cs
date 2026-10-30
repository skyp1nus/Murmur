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
/// Cancelling or a failure leaves the choice and offers the download again. Nothing downloads without a click.</para>
/// <para><b>The runtime items.</b> The GPU builds are unlisted items of the model manager (<see cref="IModelManager.Catalog"/> does
/// not list them, <see cref="IModelManager.GetStatus"/> and <see cref="IModelManager.InstallAsync"/> accept their ids). The ids
/// are derived from the listed CPU runtime by the catalog's naming (<c>llama-cpp-&lt;tag&gt;</c> gives <c>llama-cpp-vulkan-&lt;tag&gt;</c> and
/// <c>llama-cpp-cuda-&lt;tag&gt;</c>), see <see cref="RuntimeIdOf"/>; a build the manager does not know has nothing to download.</para>
/// </remarks>
public sealed partial class AccelerationSettingsViewModel : ObservableObject, IDisposable
{
    private const string CpuRuntimePrefix = "llama-cpp-";

    private readonly SettingsWriter _writer;
    private readonly IModelManager? _manager;
    private readonly IAccelerationProbe? _probe;
    private readonly IAccelerationStatusSource? _source;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _cpuRuntimeId;
    private CancellationTokenSource? _download;
    private bool _downloadFailed;
    private bool _disposed;

    internal AccelerationSettingsViewModel(
        SettingsWriter writer,
        IModelManager? manager,
        IAccelerationProbe? probe,
        IAccelerationStatusSource? source,
        Action<Action> post)
    {
        _writer = writer;
        _manager = manager;
        _probe = probe;
        _source = source;
        _post = post;
        _cpuRuntimeId = manager?.Catalog.FirstOrDefault(m => m.Kind == ModelKind.LlmRuntime)?.Id;
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
    internal void ReloadSaved() => Refresh();

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

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal void Choose(AccelerationOptionViewModel option)
    {
        if (_disposed || !option.IsAvailable || option.Value == _writer.Current.Acceleration)
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
    private void CancelDownload() => _download?.Cancel();

    /// <summary>The id of the runtime item of a GPU build in the model manager, or <c>null</c> (CPU, or no such item).</summary>
    internal string? RuntimeIdOf(AiAcceleration acceleration)
    {
        if (acceleration == AiAcceleration.Cpu || _manager is null || _cpuRuntimeId is null
            || !_cpuRuntimeId.StartsWith(CpuRuntimePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var tag = _cpuRuntimeId[CpuRuntimePrefix.Length..];
        var id = CpuRuntimePrefix + (acceleration == AiAcceleration.Vulkan ? "vulkan-" : "cuda-") + tag;
        return StatusOf(id) is null ? null : id;
    }

    private ModelStatus? StatusOf(string id)
    {
        try
        {
            return _manager?.GetStatus(id);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            // An item this OS does not have (CUDA outside Windows): nothing to download.
            return null;
        }
    }

    private async Task ChooseAsync(AiAcceleration value)
    {
        Error = null;
        _downloadFailed = false;
        var saveError = await _writer.UpdateAsync(s => s with { Acceleration = value }).ConfigureAwait(true);
        if (saveError is not null)
        {
            Error = UiText.Format(Strings.Accel_SaveFailed, saveError);
        }

        Refresh();
        if (saveError is null && RuntimeIdOf(value) is { } id && StatusOf(id) is { State: not ModelInstallState.Installed and not ModelInstallState.Downloading })
        {
            await DownloadAsync(value, id).ConfigureAwait(true);
        }
    }

    private async Task DownloadAsync(AiAcceleration acceleration, string id)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = cts;
        _downloadFailed = false;
        Error = null;
        IsDownloading = true;
        PercentText = UiText.Format(Strings.Common_Percent, 0);
        Percent = 0;
        TransferText = string.Empty;
        NoteText = UiText.Format(Strings.Accel_Downloading, LabelOf(acceleration));
        try
        {
            await _manager!.InstallAsync(id, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: the partial download stays for the next try; the card offers "Download" again.
        }
        catch (Exception ex)
        {
            Trace.TraceError("Downloading the runtime '{0}' failed: {1}", id, ex);
            _downloadFailed = true;
        }
        finally
        {
            _download = null;
            IsDownloading = false;
        }

        if (_disposed)
        {
            return;
        }

        if (_downloadFailed)
        {
            Error = UiText.Format(Strings.Accel_DownloadFailed, LabelOf(acceleration));
        }

        UpdateDownloadState(_writer.Current.Acceleration);
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

            var selected = _writer.Current.Acceleration;
            if (status.State == ModelInstallState.Downloading && RuntimeIdOf(selected) == status.Model.Id)
            {
                ShowProgress(selected, status);
            }
            else if (!IsDownloading)
            {
                UpdateDownloadState(selected);
            }
        });
    }

    private void OnStatusChanged(AccelerationStatus status) => _post(() =>
    {
        if (!_disposed && !IsDownloading)
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

    /// <summary>What the card says for the chosen build now: a missing runtime, a GPU that failed to start, or the GPU that runs it.</summary>
    private void UpdateDownloadState(AiAcceleration selected)
    {
        var label = LabelOf(selected);
        var runtime = RuntimeIdOf(selected) is { } id ? StatusOf(id) : null;
        var missing = runtime is { State: not ModelInstallState.Installed };
        if (runtime is { State: ModelInstallState.Downloading } && !IsDownloading)
        {
            // Started elsewhere (another window of the same run, the model list): show its progress.
            ShowProgress(selected, runtime);
            NeedsDownload = false;
            return;
        }

        NeedsDownload = missing && !IsDownloading;
        if (IsDownloading)
        {
            return;
        }

        var status = _source?.Current;
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
