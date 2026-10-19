using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Models;

namespace Murmur.App.ViewModels;

public enum ModelRowState
{
    NotInstalled,

    /// <summary>Waiting for an item it depends on (a chat model waits for the runtime and the embedding model).</summary>
    Queued,
    Downloading,
    Installed,
    Failed,
}

/// <summary>One row of the "Моделі" card: an item of the model catalog and what the user can do with it.</summary>
public sealed partial class ModelRowViewModel : ObservableObject
{
    private readonly ModelsSettingsViewModel _owner;

    internal ModelRowViewModel(ModelInfo info, string description, ModelsSettingsViewModel owner)
    {
        Info = info;
        Description = description;
        _owner = owner;
        SizeText = UiFormat.Size(info.SizeBytes);
    }

    public ModelInfo Info { get; }

    public string Id => Info.Id;

    public ModelKind Kind => Info.Kind;

    public string Name => Info.DisplayName;

    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(IsBusy), nameof(ShowSize), nameof(ShowProgress), nameof(ShowInstall), nameof(ShowCancel), nameof(ShowRemove), nameof(ShowMakeDefault), nameof(IsQueued), nameof(InstallLabel), nameof(CanRemoveOrInstall))]
    public partial ModelRowState State { get; set; }

    /// <summary>Download progress, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary><c>42 %</c> while downloading.</summary>
    [ObservableProperty]
    public partial string PercentText { get; set; } = string.Empty;

    /// <summary>The size shown at the right: the catalog size, replaced by the real size once the server or the disk knows it.</summary>
    [ObservableProperty]
    public partial string SizeText { get; set; }

    /// <summary><c>1,20 ГБ із 5,00 ГБ</c> while downloading.</summary>
    [ObservableProperty]
    public partial string TransferText { get; set; } = string.Empty;

    /// <summary>The active chat model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(HasBadge), nameof(ShowMakeDefault))]
    public partial bool IsDefault { get; set; }

    /// <summary>The chat model suggested for this machine while none is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(HasBadge))]
    public partial bool IsRecommended { get; set; }

    /// <summary>A short inline error (download failed, removal failed), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>The first click on "Видалити" asks to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemove), nameof(CanRemoveOrInstall))]
    public partial bool IsConfirmingRemove { get; set; }

    /// <summary>The row belongs to a running AI bundle (or is a runtime/embedding model one needs): it cannot be removed now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemove), nameof(CanRemoveOrInstall))]
    public partial bool IsRemoveLocked { get; set; }

    public bool IsInstalled => State == ModelRowState.Installed;

    public bool IsQueued => State == ModelRowState.Queued;

    /// <summary>Downloading or waiting for a dependency: it can be cancelled.</summary>
    public bool IsBusy => State is ModelRowState.Downloading or ModelRowState.Queued;

    public bool ShowSize => !IsBusy;

    public bool ShowProgress => State == ModelRowState.Downloading;

    public bool ShowInstall => State is ModelRowState.NotInstalled or ModelRowState.Failed;

    public bool ShowCancel => IsBusy;

    public bool ShowRemove => IsInstalled && !IsConfirmingRemove && !IsRemoveLocked;

    public bool CanRemoveOrInstall => (IsInstalled && !IsRemoveLocked) || ShowInstall;

    public bool ShowMakeDefault => IsInstalled && Kind == ModelKind.Chat && !IsDefault;

    public bool HasError => Error is not null;

    public string InstallLabel => State == ModelRowState.Failed ? Strings.Models_Retry : Strings.Models_Install;

    public bool HasBadge => IsDefault || IsRecommended;

    public string BadgeText => IsDefault ? Strings.Models_Default : IsRecommended ? Strings.Models_Recommended : string.Empty;

    /// <summary>The label of the "queued" state; also what the screen reader reads.</summary>
    public string QueuedText => Strings.Models_Queued;

    public string InstalledText => Strings.Models_Installed;

    [RelayCommand]
    private void Install() => _owner.Install(this);

    [RelayCommand]
    private void Cancel() => _owner.Cancel(this);

    [RelayCommand]
    private void RequestRemove() => IsConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private void ConfirmRemove() => _owner.Remove(this);

    [RelayCommand]
    private void MakeDefault() => _owner.MakeDefault(this);

    /// <summary>Takes over a status of the model manager (the state, the progress and the sizes).</summary>
    internal void Apply(ModelStatus status)
    {
        switch (status.State)
        {
            case ModelInstallState.Downloading:
                State = ModelRowState.Downloading;
                Error = null;
                ShowProgressOf(status);
                break;
            case ModelInstallState.Installed:
                State = ModelRowState.Installed;
                Error = null;
                Percent = 100;
                PercentText = string.Empty;
                TransferText = string.Empty;
                IsConfirmingRemove = false;
                var size = status.BytesTotal ?? status.BytesDone;
                SizeText = UiFormat.Size(size > 0 ? size : Info.SizeBytes);
                break;
            case ModelInstallState.Failed:
                MarkFailed(null, status.Error);
                break;
            default:
                // A waiting row stays waiting until its own install starts; the manager knows nothing of the queue.
                if (State != ModelRowState.Queued)
                {
                    State = ModelRowState.NotInstalled;
                    Percent = 0;
                    PercentText = string.Empty;
                    TransferText = string.Empty;
                    IsConfirmingRemove = false;
                    SizeText = UiFormat.Size(Info.SizeBytes);
                }

                break;
        }
    }

    internal void MarkQueued()
    {
        if (State is ModelRowState.NotInstalled or ModelRowState.Failed)
        {
            State = ModelRowState.Queued;
            Error = null;
        }
    }

    /// <summary>
    /// The install failed. The text is neutral (a checksum or disk failure is not a network problem); only a full disk, which
    /// the manager and the file system report recognisably, gets its own text. The details stay in the log.
    /// </summary>
    internal void MarkFailed(Exception? exception = null, string? detail = null)
    {
        State = ModelRowState.Failed;
        Error = IsOutOfSpace(exception, detail) ? Strings.Models_NoSpace : Strings.Models_InstallFailed;
        PercentText = string.Empty;
        TransferText = string.Empty;
    }

    private bool IsOutOfSpace(Exception? exception, string? detail)
    {
        // The main signal does not depend on any wording: the drive has less room than the item still needs.
        if (_owner.IsLowOnSpace(this))
        {
            return true;
        }

        // Fallbacks: the file system's own "disk full" (full HRESULTs on Windows, errno 28 on Unix), then Core's wording.
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is IOException io && IsDiskFull(io.HResult))
            {
                return true;
            }

            if (Mentions(ex.Message))
            {
                return true;
            }
        }

        return Mentions(detail);

        static bool IsDiskFull(int hresult) => OperatingSystem.IsWindows()
            ? hresult is unchecked((int)0x80070070) or unchecked((int)0x80070027)
            : hresult == 28;

        static bool Mentions(string? text) =>
            text is not null
            && (text.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase)
                || text.Contains("no space left", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                || text.Contains("disk is full", StringComparison.OrdinalIgnoreCase));
    }

    private void ShowProgressOf(ModelStatus status)
    {
        var total = status.BytesTotal is > 0 ? status.BytesTotal.Value : Info.SizeBytes;
        var fraction = total > 0 ? Math.Clamp(status.BytesDone / (double)total, 0, 1) : 0;
        Percent = fraction * 100;
        PercentText = UiText.Format(Strings.Common_Percent, (int)Percent);
        TransferText = UiText.Format(Strings.Models_Downloaded, UiFormat.Size(status.BytesDone), UiFormat.Size(total));
        if (status.BytesTotal is > 0)
        {
            SizeText = UiFormat.Size(status.BytesTotal.Value);
        }
    }
}
