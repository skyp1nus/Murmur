namespace Murmur.Core.Settings;

public enum ThemePreference
{
    /// <summary>Follow the OS light/dark setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// User preferences that survive restarts. Missing values in a stored file take these defaults, so new properties
/// can be added without a migration; a change of meaning of an existing one is a migration keyed on <see cref="SettingsVersion"/>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// The layout version of the stored settings; the current one is 2. A file without it (or with a lower value) was written
    /// before D-039, when <c>"uk"</c> in <see cref="Language"/> was only the old default: the store loads it with
    /// <c>Language = "en"</c> and writes version 2 on the next save. A file with version 2 keeps the user's choice.
    /// </summary>
    public int SettingsVersion { get; init; } = 2;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>UI language, a two-letter culture name: <c>en</c> (default) or <c>uk</c>. Applied on the next start.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Set when the first-run onboarding has been finished or skipped.</summary>
    public bool OnboardingCompleted { get; init; }

    /// <summary>The microphone is recorded by default when a new recording starts (the Microphone switch of the Library).</summary>
    public bool RecordMicrophoneByDefault { get; init; } = true;

    /// <summary>System audio is recorded by default when a new recording starts.</summary>
    public bool RecordSystemAudioByDefault { get; init; } = true;

    /// <summary>Screen recording is on by default when a new recording starts.</summary>
    public bool ScreenByDefault { get; init; }

    /// <summary><c>null</c> = system default device.</summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary><c>null</c> = system default output.</summary>
    public string? SystemAudioDeviceId { get; init; }

    /// <summary>Transcription language: <c>auto</c> or a two-letter culture name.</summary>
    public string TranscriptionLanguage { get; init; } = "auto";

    /// <summary>Global start/stop hotkey in Avalonia gesture syntax (e.g. <c>Ctrl+Shift+R</c>); <c>null</c> = none.</summary>
    public string? RecordHotkey { get; init; } = "Ctrl+Shift+R";

    /// <summary>Global hotkey that turns screen recording on/off during a recording; <c>null</c> = none.</summary>
    public string? ToggleScreenHotkey { get; init; } = "Ctrl+Shift+S";

    /// <summary>Global hotkey that brings the Murmur window to the front; <c>null</c> = none.</summary>
    public string? ShowAppHotkey { get; init; } = "Ctrl+Shift+M";

    /// <summary>Global hotkey that pauses and resumes the running recording; <c>null</c> = none.</summary>
    public string? PauseHotkey { get; init; } = "Ctrl+Shift+P";

    /// <summary>Display recorded when the screen is on; <c>null</c> = the primary display.</summary>
    public string? ScreenDisplayId { get; init; }

    /// <summary>Where new recordings go; <c>null</c> = the default data folder.</summary>
    public string? RecordingsDirectory { get; init; }

    /// <summary>The active chat model id from the model catalog; <c>null</c> = none chosen (AI skipped).</summary>
    public string? ChatModelId { get; init; }

    /// <summary>Which <c>llama-server</c> build runs the chat model; the CPU build is the default and the fallback.</summary>
    public AiAcceleration Acceleration { get; init; } = AiAcceleration.Cpu;
}

/// <summary>Loads and saves <see cref="AppSettings"/>. Thread-safe; <see cref="Changed"/> may be raised off the UI thread.</summary>
public interface ISettingsStore
{
    /// <summary>The current settings; never <c>null</c> (defaults when nothing is stored or the file is unreadable).</summary>
    AppSettings Current { get; }

    /// <summary>Replaces the settings and writes them to disk atomically.</summary>
    ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    event Action<AppSettings>? Changed;
}
