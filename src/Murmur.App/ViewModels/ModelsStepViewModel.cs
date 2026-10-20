using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Models;

namespace Murmur.App.ViewModels;

public enum RequiredModelsState
{
    /// <summary>Not started yet (the step has not been opened).</summary>
    Waiting,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Step 3: the required transcription model (Parakeet and the voice-activity model) installs by itself when the step
/// opens; the AI model is chosen by the user. Everything goes through the rows of the Settings' model list
/// (<see cref="ModelsSettingsViewModel"/>), so an install started here is the very one the Settings screen shows, runs on
/// the app-lifetime tokens and keeps going after the onboarding.
/// </summary>
/// <remarks>
/// Choosing a chat model starts <see cref="ModelsSettingsViewModel.Install"/> for it: the same bundle (runtime, embedding
/// model, chat model) as the Settings. Before it starts, the free space of the models drive is compared with what the
/// bundle still needs.
/// </remarks>
public sealed partial class ModelsStepViewModel : OnboardingStepViewModel
{
    private readonly ModelsSettingsViewModel? _models;
    private readonly IModelManager? _manager;
    private readonly List<ModelRowViewModel> _required = [];

    /// <summary>
    /// Free bytes of the models drive, read when the step opens, when the drive's free space changes (an install ended) and
    /// when a choice is made; never on a progress tick. -1 = not read yet or unknown.
    /// </summary>
    private long _freeBytes = -1;

    internal ModelsStepViewModel(ModelsSettingsViewModel? models, IModelManager? manager)
    {
        _models = models;
        _manager = manager;
        if (models is null)
        {
            return;
        }

        var chats = models.Rows.Where(r => r.Kind == ModelKind.Chat).ToList();
        var largest = chats.Count > 1 ? chats.MaxBy(r => r.Info.SizeBytes) : null;
        var recommended = manager?.RecommendedChatModelId;
        var choices = new List<AiChoiceViewModel>(chats.Count);
        foreach (var row in chats)
        {
            var isRecommended = row.Id == recommended;
            var description = isRecommended ? Strings.Onb3_Recommended : ReferenceEquals(row, largest) ? Strings.Onb3_BestQuality : Strings.Onb3_Light;
            choices.Add(new AiChoiceViewModel(row, description, isRecommended, this));
        }

        Choices = choices;
        foreach (var row in models.Rows)
        {
            if (row.Info.Required)
            {
                _required.Add(row);
            }

            row.PropertyChanged += OnRowChanged;
        }

        models.PropertyChanged += OnModelsChanged;
        RequiredSizeText = UiFormat.Size(_required.Sum(r => r.Info.SizeBytes));
        Recompute();
    }

    public override string Title => Strings.Onb3_Headline;

    public override string Subtitle => Strings.Onb3_Subline;

    /// <summary>A model manager is wired; without one the step only explains that downloads are not available.</summary>
    public bool IsAvailable => _models is not null;

    public bool IsUnavailable => _models is null;

    public IReadOnlyList<AiChoiceViewModel> Choices { get; } = [];

    /// <summary>The required models' total size, e.g. "0.67 GB".</summary>
    public string RequiredSizeText { get; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRequiredReady), nameof(IsRequiredFailed), nameof(ShowRequiredProgress), nameof(RequiredStatusText))]
    public partial RequiredModelsState RequiredState { get; set; }

    /// <summary>Combined progress of the required models, 0 to 100, weighted by size.</summary>
    [ObservableProperty]
    public partial double RequiredPercent { get; set; }

    /// <summary>"0.31 GB / 0.67 GB" while downloading.</summary>
    [ObservableProperty]
    public partial string RequiredTransferText { get; set; } = string.Empty;

    public bool IsRequiredReady => RequiredState == RequiredModelsState.Ready;

    public bool IsRequiredFailed => RequiredState == RequiredModelsState.Failed;

    public bool ShowRequiredProgress => RequiredState is RequiredModelsState.Waiting or RequiredModelsState.Downloading;

    public string RequiredStatusText => RequiredState switch
    {
        RequiredModelsState.Failed => Strings.Onb3_Failed,
        RequiredModelsState.Waiting => Strings.Onb3_Waiting,
        _ => Strings.Onb3_Downloading,
    };

    /// <summary>"Skip AI for now" is the chosen option.</summary>
    [ObservableProperty]
    public partial bool SkipAiSelected { get; set; }

    /// <summary>Why the last choice did not start (the drive is too small), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpaceMessage))]
    public partial string? SpaceMessage { get; set; }

    public bool HasSpaceMessage => SpaceMessage is not null;

    /// <summary>"Free on disk: 123.46 GB", or empty when unknown.</summary>
    public string FreeSpaceText => _models?.FreeSpaceText ?? string.Empty;

    public bool HasFreeSpace => FreeSpaceText.Length > 0;

    /// <summary>Everything that was chosen is installed: the "Ready" variant of the step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundNote))]
    public partial bool AllDone { get; set; }

    public bool ShowBackgroundNote => !AllDone;

    /// <summary>A chat model is installed, so the cards that are not say "Not downloaded".</summary>
    internal bool AnyChatInstalled => Choices.Any(c => c.IsInstalled);

    /// <summary>The required models are installed.</summary>
    internal bool RequiredReady => RequiredState == RequiredModelsState.Ready;

    /// <summary>Raised on the UI thread whenever the state of the required models changes.</summary>
    internal event Action? RequiredChanged;

    internal override void Activate()
    {
        if (_models is null)
        {
            return;
        }

        _models.Refresh();
        RefreshFreeBytes();
        if (!SkipAiSelected && !Choices.Any(c => c.IsSelected))
        {
            // Come back to a choice made earlier (or started in the Settings): the busy or the active chat model.
            var current = Choices.FirstOrDefault(c => c.IsBusy) ?? Choices.FirstOrDefault(c => c.Row.IsDefault) ?? Choices.FirstOrDefault(c => c.IsInstalled);
            if (current is not null)
            {
                current.IsSelected = true;
            }
        }

        StartRequired();
        Recompute();
    }

    [RelayCommand]
    private void RetryRequired() => StartRequired();

    [RelayCommand]
    private void SkipAi()
    {
        if (_models is null)
        {
            return;
        }

        SkipAiSelected = true;
        SpaceMessage = null;
        foreach (var choice in Choices)
        {
            choice.IsSelected = false;
            if (choice.IsBusy)
            {
                // Cancelling the bundle keeps the partial downloads for later; the Settings resume them.
                _models.Cancel(choice.Row);
            }
        }

        Recompute();
    }

    internal void Select(AiChoiceViewModel choice)
    {
        if (_models is null || !choice.IsEnabled)
        {
            return;
        }

        var row = choice.Row;
        if (choice.IsSelected)
        {
            // Pressing the chosen card again after a failure tries again.
            if (row.State == ModelRowState.Failed)
            {
                _models.Install(row);
            }

            return;
        }

        if (!row.IsInstalled && !row.IsBusy)
        {
            RefreshFreeBytes();
            var need = BytesStillNeeded(row);
            if (!HasSpaceFor(need))
            {
                SpaceMessage = UiText.Format(Strings.Onb3_NoSpace, UiFormat.Size(need), UiFormat.Size(_freeBytes));
                return;
            }
        }

        SpaceMessage = null;
        SkipAiSelected = false;
        foreach (var other in Choices)
        {
            other.IsSelected = ReferenceEquals(other, choice);
        }

        if (!row.IsInstalled && !row.IsBusy)
        {
            _models.Install(row);
        }

        Recompute();
    }

    private void StartRequired()
    {
        if (_models is null)
        {
            return;
        }

        foreach (var row in _required)
        {
            if (row.State is ModelRowState.NotInstalled or ModelRowState.Failed)
            {
                _models.Install(row);
            }
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelRowViewModel.State) or nameof(ModelRowViewModel.Percent))
        {
            Recompute();
        }
    }

    private void OnModelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelsSettingsViewModel.FreeSpaceText))
        {
            OnPropertyChanged(nameof(FreeSpaceText));
            OnPropertyChanged(nameof(HasFreeSpace));
            RefreshFreeBytes();
            Recompute();
        }
    }

    /// <summary>Re-derives the required card, the locks and the "ready" variant from the rows.</summary>
    private void Recompute()
    {
        if (_models is null)
        {
            return;
        }

        var before = RequiredState;
        var total = 0L;
        var done = 0.0;
        var anyFailed = false;
        var anyBusy = false;
        var allInstalled = true;
        foreach (var row in _required)
        {
            total += row.Info.SizeBytes;
            done += row.IsInstalled ? row.Info.SizeBytes : row.Info.SizeBytes * Math.Clamp(row.Percent, 0, 100) / 100;
            anyFailed |= row.State == ModelRowState.Failed;
            anyBusy |= row.IsBusy;
            allInstalled &= row.IsInstalled;
        }

        RequiredState = allInstalled ? RequiredModelsState.Ready
            : anyFailed ? RequiredModelsState.Failed
            : anyBusy ? RequiredModelsState.Downloading
            : RequiredModelsState.Waiting;
        RequiredPercent = total > 0 ? done * 100 / total : 100;
        RequiredTransferText = RequiredState == RequiredModelsState.Downloading
            ? UiFormat.SizeProgress((long)done, total)
            : string.Empty;

        var chatBusy = Choices.Any(c => c.IsBusy);
        var selected = Choices.FirstOrDefault(c => c.IsSelected);
        foreach (var choice in Choices)
        {
            choice.IsEnabled = !Choices.Any(other => !ReferenceEquals(other, choice) && other.IsBusy);
            choice.SpaceText = choice.Row.State == ModelRowState.NotInstalled && !HasSpaceFor(BytesStillNeeded(choice.Row))
                ? UiText.Format(Strings.Onb3_NoSpace, UiFormat.Size(BytesStillNeeded(choice.Row)), UiFormat.Size(_freeBytes))
                : null;
            choice.Notify();
        }

        AllDone = allInstalled && !chatBusy && (selected is null || selected.IsInstalled);
        if (before != RequiredState)
        {
            RequiredChanged?.Invoke();
        }
    }

    /// <summary>What installing <paramref name="chat"/> still has to download: the missing runtime and embedding model, the model itself and the rest of the required ones.</summary>
    private long BytesStillNeeded(ModelRowViewModel chat)
    {
        var bytes = chat.Info.SizeBytes;
        foreach (var row in _models!.Rows)
        {
            if (!row.IsInstalled && (row.Kind is ModelKind.LlmRuntime or ModelKind.Embedding))
            {
                bytes += row.Info.SizeBytes;
            }
        }

        foreach (var row in _required)
        {
            if (!row.IsInstalled)
            {
                bytes += (long)(row.Info.SizeBytes * (1 - (Math.Clamp(row.Percent, 0, 100) / 100)));
            }
        }

        return bytes;
    }

    private void RefreshFreeBytes() => _freeBytes = _manager?.AvailableFreeSpaceBytes ?? -1;

    /// <summary>The models drive can hold <paramref name="bytes"/>; an unknown amount of free space never blocks.</summary>
    private bool HasSpaceFor(long bytes) => _freeBytes < 0 || _freeBytes == long.MaxValue || bytes <= _freeBytes;
}
