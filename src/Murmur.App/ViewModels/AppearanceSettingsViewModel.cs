using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The "Вигляд" section: theme (applied at once by the <c>ThemeApplier</c> when the setting is saved), language (applies
/// after a restart, decision D-032) and "go through the introduction again".
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ObservableObject
{
    private readonly SettingsWriter _writer;
    private bool _loading;

    internal AppearanceSettingsViewModel(SettingsWriter writer)
    {
        _writer = writer;
        Refresh();
    }

    /// <summary>The only transcription language of the MVP: Parakeet detects it by itself (the same choice as the onboarding's).</summary>
    public IReadOnlyList<string> TranscriptionLanguages { get; } = [Strings.Onb4_Auto];

    [ObservableProperty]
    public partial ThemePreference Theme { get; set; }

    /// <summary><c>en</c> or <c>uk</c>.</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = UiCulture.English;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>The introduction was reset in this visit: it appears on the next start.</summary>
    [ObservableProperty]
    public partial bool OnboardingReset { get; set; }

    public bool HasError => Error is not null;

    public bool IsLight
    {
        get => Theme == ThemePreference.Light;
        set => SelectTheme(ThemePreference.Light, value, nameof(IsLight));
    }

    public bool IsDark
    {
        get => Theme == ThemePreference.Dark;
        set => SelectTheme(ThemePreference.Dark, value, nameof(IsDark));
    }

    public bool IsSystem
    {
        get => Theme == ThemePreference.System;
        set => SelectTheme(ThemePreference.System, value, nameof(IsSystem));
    }

    public bool IsUkrainian
    {
        get => Language == UiCulture.Ukrainian;
        set => SelectLanguage(UiCulture.Ukrainian, value, nameof(IsUkrainian));
    }

    public bool IsEnglish
    {
        get => Language == UiCulture.English;
        set => SelectLanguage(UiCulture.English, value, nameof(IsEnglish));
    }

    /// <summary>The chosen language is not the one the app runs in.</summary>
    public bool NeedsRestart => Language != UiCulture.Language;

    /// <summary>The last save started (for tests).</summary>
    internal Task LastSave { get; private set; } = Task.CompletedTask;

    internal void Refresh()
    {
        var settings = _writer.Current;
        _loading = true;
        try
        {
            Theme = settings.Theme;
            Language = UiCulture.Normalize(settings.Language);
            OnboardingReset = !settings.OnboardingCompleted;
            Error = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The store changed elsewhere: shows the saved values; the inline error stays.</summary>
    internal void ReloadSaved()
    {
        var settings = _writer.Current;
        _loading = true;
        try
        {
            Theme = settings.Theme;
            Language = UiCulture.Normalize(settings.Language);
            OnboardingReset = !settings.OnboardingCompleted;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void RestartOnboarding()
    {
        OnboardingReset = true;
        LastSave = SaveAsync(s => s with { OnboardingCompleted = false });
    }

    partial void OnThemeChanged(ThemePreference value)
    {
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsSystem));
        if (!_loading)
        {
            LastSave = SaveAsync(s => s with { Theme = value });
        }
    }

    partial void OnLanguageChanged(string value)
    {
        OnPropertyChanged(nameof(IsUkrainian));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(NeedsRestart));
        if (!_loading)
        {
            LastSave = SaveAsync(s => s with { Language = value });
        }
    }

    private void SelectTheme(ThemePreference theme, bool selected, string property)
    {
        if (selected)
        {
            Theme = theme;
        }
        else
        {
            // The toggle was clicked while checked: it stays checked.
            OnPropertyChanged(property);
        }
    }

    private void SelectLanguage(string language, bool selected, string property)
    {
        if (selected)
        {
            Language = language;
        }
        else
        {
            OnPropertyChanged(property);
        }
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> edit)
    {
        Error = await _writer.UpdateAsync(edit).ConfigureAwait(true);
    }
}
