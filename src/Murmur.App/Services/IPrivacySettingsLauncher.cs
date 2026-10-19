using System.ComponentModel;
using System.Diagnostics;

namespace Murmur.App.Services;

/// <summary>Opens the operating system page where microphone access is granted.</summary>
public interface IPrivacySettingsLauncher
{
    /// <summary>The platform has such a page (Windows); the button that opens it is hidden elsewhere.</summary>
    bool CanOpenMicrophoneSettings { get; }

    /// <summary>Opens the microphone privacy page; returns an error text (English, for inline messages) or <c>null</c> on success.</summary>
    string? TryOpenMicrophoneSettings();
}

/// <summary>
/// <see cref="IPrivacySettingsLauncher"/> over the Windows settings URI <c>ms-settings:privacy-microphone</c>; there is no
/// such page to open on the other platforms. Windows has no permission prompt for desktop apps, so this page is the only
/// way to grant access. UNVERIFIED-ON-WINDOWS: it cannot be run on Linux.
/// </summary>
public sealed class SystemPrivacySettingsLauncher : IPrivacySettingsLauncher
{
    public static readonly SystemPrivacySettingsLauncher Instance = new();

    private const string MicrophoneUri = "ms-settings:privacy-microphone";

    public bool CanOpenMicrophoneSettings => OperatingSystem.IsWindows();

    public string? TryOpenMicrophoneSettings()
    {
        if (!CanOpenMicrophoneSettings)
        {
            return "The microphone settings page exists on Windows only.";
        }

        try
        {
            // The shell resolves the URI scheme; nobody waits for the Settings app.
            using var process = Process.Start(new ProcessStartInfo(MicrophoneUri) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return ex.Message;
        }
    }
}
