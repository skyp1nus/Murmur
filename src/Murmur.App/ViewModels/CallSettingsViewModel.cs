using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The "Detect calls" row of Settings → Devices (the recording settings): the switch, saved at once, and "Try it", which shows
/// the call toast for a sample app. The row exists only where the platform can watch the audio sessions.
/// </summary>
public sealed partial class CallSettingsViewModel : ObservableObject
{
    private readonly SettingsWriter _writer;
    private readonly Action _tryIt;
    private bool _loading;

    internal CallSettingsViewModel(SettingsWriter writer, Action tryIt)
    {
        _writer = writer;
        _tryIt = tryIt;
        ReloadSaved();
    }

    [ObservableProperty]
    public partial bool DetectCalls { get; set; }

    /// <summary>Inline error of the last save, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The last save started by the switch (for tests).</summary>
    internal Task LastSave { get; private set; } = Task.CompletedTask;

    internal void ReloadSaved()
    {
        _loading = true;
        try
        {
            DetectCalls = _writer.Current.DetectCalls;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnDetectCallsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        LastSave = SaveAsync(value);
    }

    private async Task SaveAsync(bool value) => Error = await _writer.UpdateAsync(s => s with { DetectCalls = value });

    [RelayCommand]
    private void TryIt() => _tryIt();
}
