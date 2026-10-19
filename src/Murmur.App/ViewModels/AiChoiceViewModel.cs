using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>One selectable AI model card of the models step: a chat model of the catalog with its install progress.</summary>
public sealed partial class AiChoiceViewModel : ObservableObject
{
    private readonly ModelsStepViewModel _owner;

    internal AiChoiceViewModel(ModelRowViewModel row, string description, bool isRecommended, ModelsStepViewModel owner)
    {
        Row = row;
        Description = description;
        IsRecommended = isRecommended;
        _owner = owner;
        row.PropertyChanged += OnRowChanged;
    }

    internal ModelRowViewModel Row { get; }

    public string Name => Row.Name;

    /// <summary>"Recommended for your PC", "Best quality, needs GPU" or the short light-weight text.</summary>
    public string Description { get; }

    /// <summary>The catalog size, replaced by the real one once the server knows it.</summary>
    public string SizeText => Row.SizeText;

    /// <summary>This is the model <see cref="Murmur.Core.Models.IModelManager.RecommendedChatModelId"/> names.</summary>
    public bool IsRecommended { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The cards are locked while another model's bundle is downloading (it is cancelled with "Skip AI for now").</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>The drive cannot hold this choice; the card says so and refuses to start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus))]
    public partial string? SpaceText { get; set; }

    public bool IsInstalled => Row.IsInstalled;

    public bool IsBusy => Row.IsBusy;

    public bool ShowProgress => Row.State == ModelRowState.Downloading;

    public double Percent => Row.Percent;

    /// <summary>A line under the description: progress, "waiting", the failure or the lack of space; <c>null</c> = none.</summary>
    public string? StatusText => Row.State switch
    {
        ModelRowState.Downloading => UiText.Format(Strings.Onb3_DownloadingPercent, Row.PercentText),
        ModelRowState.Queued => Strings.Onb3_WaitingForParts,
        ModelRowState.Failed => Strings.Onb3_FailedRetry,
        ModelRowState.NotInstalled when SpaceText is not null => SpaceText,
        ModelRowState.NotInstalled when _owner.AnyChatInstalled => Strings.Onb3_NotDownloaded,
        _ => null,
    };

    public bool HasStatus => StatusText is not null;

    [RelayCommand]
    private void Select() => _owner.Select(this);

    internal void Notify()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ModelRowViewModel.State):
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowProgress));
                Notify();
                break;
            case nameof(ModelRowViewModel.Percent):
                OnPropertyChanged(nameof(Percent));
                break;
            case nameof(ModelRowViewModel.PercentText):
                Notify();
                break;
            case nameof(ModelRowViewModel.SizeText):
                OnPropertyChanged(nameof(SizeText));
                break;
        }
    }
}
