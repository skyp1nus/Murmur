using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Audio;

/// <summary>
/// Translates E_ACCESSDENIED from IMMDevice.Activate / IAudioClient.Initialize into an
/// <see cref="UnauthorizedAccessException"/>: Windows returns it for a capture endpoint when the privacy settings deny
/// microphone access to desktop apps (observed in CI at IAudioClient.Initialize, HRESULT 0x80070005).
/// </summary>
internal static class AudioAccess
{
    public const string MicrophoneDeniedMessage =
        "Microphone access is denied by Windows privacy settings (Settings → Privacy & security → Microphone → Let desktop apps access your microphone)";

    public const string EndpointDeniedMessage = "Access to the audio endpoint is denied by Windows";

    /// <summary>True when <paramref name="error"/> or one of its inner exceptions carries E_ACCESSDENIED.</summary>
    public static bool IsAccessDenied(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e.HResult == WasapiConstants.EAccessDenied)
            {
                return true;
            }
        }

        return false;
    }

    public static UnauthorizedAccessException Denied(AudioDeviceKind kind, string deviceName, Exception inner)
    {
        var reason = kind == AudioDeviceKind.Microphone ? MicrophoneDeniedMessage : EndpointDeniedMessage;
        return new UnauthorizedAccessException($"{reason}. Device '{deviceName}': {inner.Message}", inner);
    }
}
