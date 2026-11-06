using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Murmur.Capture.Windows.Audio.Interop;

// audiopolicy.h / mmdeviceapi.h interfaces of call detection (#44), source-generated like ComInterfaces.cs: methods in the order
// of the C "<Name>Vtbl" structs of the Windows SDK 10.0.26100 headers, IUnknown implied (the first method is slot 3), every
// method [PreserveSig]. The "2" interfaces are declared flat (their base's methods first) instead of inheriting, so each slot
// number below can be read off directly. Interface pointers and strings travel as nint and are handled by the caller.

/// <summary>audiopolicy.h IAudioSessionManager2, IID 77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F (slots 3-9).</summary>
[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    // [3] IAudioSessionManager::GetAudioSessionControl(LPCGUID AudioSessionGuid, DWORD StreamFlags, IAudioSessionControl **SessionControl)
    [PreserveSig]
    int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl);

    // [4] IAudioSessionManager::GetSimpleAudioVolume(LPCGUID AudioSessionGuid, DWORD StreamFlags, ISimpleAudioVolume **AudioVolume)
    [PreserveSig]
    int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume);

    // [5] HRESULT GetSessionEnumerator(IAudioSessionEnumerator **SessionEnum)
    [PreserveSig]
    int GetSessionEnumerator(out nint sessionEnum);

    // [6] HRESULT RegisterSessionNotification(IAudioSessionNotification *SessionNotification)
    [PreserveSig]
    int RegisterSessionNotification(nint sessionNotification);

    // [7] HRESULT UnregisterSessionNotification(IAudioSessionNotification *SessionNotification)
    [PreserveSig]
    int UnregisterSessionNotification(nint sessionNotification);

    // [8] HRESULT RegisterDuckNotification(LPCWSTR sessionID, IAudioVolumeDuckNotification *duckNotification)
    [PreserveSig]
    int RegisterDuckNotification(nint sessionId, nint duckNotification);

    // [9] HRESULT UnregisterDuckNotification(IAudioVolumeDuckNotification *duckNotification)
    [PreserveSig]
    int UnregisterDuckNotification(nint duckNotification);
}

/// <summary>audiopolicy.h IAudioSessionEnumerator, IID E2F5BB11-0570-40CA-ACDD-3AA01277DEE8 (slots 3-4).</summary>
[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    // [3] HRESULT GetCount(int *SessionCount)
    [PreserveSig]
    int GetCount(out int sessionCount);

    // [4] HRESULT GetSession(int SessionCount, IAudioSessionControl **Session)
    [PreserveSig]
    int GetSession(int index, out nint session);
}

/// <summary>audiopolicy.h IAudioSessionControl2, IID BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D (slots 3-16).</summary>
[GeneratedComInterface]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2
{
    // [3] IAudioSessionControl::GetState(AudioSessionState *pRetVal)
    [PreserveSig]
    int GetState(out int state);

    // [4] IAudioSessionControl::GetDisplayName(LPWSTR *pRetVal) -- freed with CoTaskMemFree
    [PreserveSig]
    int GetDisplayName(out nint displayName);

    // [5] IAudioSessionControl::SetDisplayName(LPCWSTR Value, LPCGUID EventContext)
    [PreserveSig]
    int SetDisplayName(nint value, nint eventContext);

    // [6] IAudioSessionControl::GetIconPath(LPWSTR *pRetVal)
    [PreserveSig]
    int GetIconPath(out nint iconPath);

    // [7] IAudioSessionControl::SetIconPath(LPCWSTR Value, LPCGUID EventContext)
    [PreserveSig]
    int SetIconPath(nint value, nint eventContext);

    // [8] IAudioSessionControl::GetGroupingParam(GUID *pRetVal)
    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    // [9] IAudioSessionControl::SetGroupingParam(LPCGUID Override, LPCGUID EventContext)
    [PreserveSig]
    int SetGroupingParam(nint groupingOverride, nint eventContext);

    // [10] IAudioSessionControl::RegisterAudioSessionNotification(IAudioSessionEvents *NewNotifications)
    [PreserveSig]
    int RegisterAudioSessionNotification(nint newNotifications);

    // [11] IAudioSessionControl::UnregisterAudioSessionNotification(IAudioSessionEvents *NewNotifications)
    [PreserveSig]
    int UnregisterAudioSessionNotification(nint newNotifications);

    // [12] HRESULT GetSessionIdentifier(LPWSTR *pRetVal)
    [PreserveSig]
    int GetSessionIdentifier(out nint identifier);

    // [13] HRESULT GetSessionInstanceIdentifier(LPWSTR *pRetVal) -- freed with CoTaskMemFree
    [PreserveSig]
    int GetSessionInstanceIdentifier(out nint identifier);

    // [14] HRESULT GetProcessId(DWORD *pRetVal) -- AUDCLNT_S_NO_SINGLE_PROCESS for a cross-process session
    [PreserveSig]
    int GetProcessId(out uint processId);

    // [15] HRESULT IsSystemSoundsSession(void) -- S_OK = yes, S_FALSE = no
    [PreserveSig]
    int IsSystemSoundsSession();

    // [16] HRESULT SetDuckingPreference(BOOL optOut)
    [PreserveSig]
    int SetDuckingPreference(int optOut);
}

/// <summary>
/// audiopolicy.h IAudioSessionNotification, IID 641DD20B-4D41-49CC-ABA3-174B9477BB08 (slot 3), implemented by
/// <see cref="SessionCallbacks"/>. Called on an audio-service thread; must not block or register anything.
/// </summary>
[GeneratedComInterface]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
internal partial interface IAudioSessionNotification
{
    // [3] HRESULT OnSessionCreated(IAudioSessionControl *NewSession)
    [PreserveSig]
    int OnSessionCreated(nint newSession);
}

/// <summary>audiopolicy.h IAudioSessionEvents, IID 24918ACC-64B3-37C1-8CA9-74A66E9957A8 (slots 3-9), implemented by <see cref="SessionCallbacks"/>.</summary>
[GeneratedComInterface]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface IAudioSessionEvents
{
    // [3] HRESULT OnDisplayNameChanged(LPCWSTR NewDisplayName, LPCGUID EventContext)
    [PreserveSig]
    int OnDisplayNameChanged(nint newDisplayName, nint eventContext);

    // [4] HRESULT OnIconPathChanged(LPCWSTR NewIconPath, LPCGUID EventContext)
    [PreserveSig]
    int OnIconPathChanged(nint newIconPath, nint eventContext);

    // [5] HRESULT OnSimpleVolumeChanged(float NewVolume, BOOL NewMute, LPCGUID EventContext)
    [PreserveSig]
    int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext);

    // [6] HRESULT OnChannelVolumeChanged(DWORD ChannelCount, float NewChannelVolumeArray[], DWORD ChangedChannel, LPCGUID EventContext)
    [PreserveSig]
    int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel, nint eventContext);

    // [7] HRESULT OnGroupingParamChanged(LPCGUID NewGroupingParam, LPCGUID EventContext)
    [PreserveSig]
    int OnGroupingParamChanged(nint newGroupingParam, nint eventContext);

    // [8] HRESULT OnStateChanged(AudioSessionState NewState)
    [PreserveSig]
    int OnStateChanged(int newState);

    // [9] HRESULT OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason)
    [PreserveSig]
    int OnSessionDisconnected(int disconnectReason);
}

/// <summary>mmdeviceapi.h IMMNotificationClient, IID 7991EEC9-7E89-4D85-8390-6C703CEC60C0 (slots 3-7), implemented by <see cref="SessionCallbacks"/>.</summary>
[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    // [3] HRESULT OnDeviceStateChanged(LPCWSTR pwstrDeviceId, DWORD dwNewState)
    [PreserveSig]
    int OnDeviceStateChanged(nint deviceId, uint newState);

    // [4] HRESULT OnDeviceAdded(LPCWSTR pwstrDeviceId)
    [PreserveSig]
    int OnDeviceAdded(nint deviceId);

    // [5] HRESULT OnDeviceRemoved(LPCWSTR pwstrDeviceId)
    [PreserveSig]
    int OnDeviceRemoved(nint deviceId);

    // [6] HRESULT OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR pwstrDefaultDeviceId)
    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId);

    // [7] HRESULT OnPropertyValueChanged(LPCWSTR pwstrDeviceId, const PROPERTYKEY key)
    [PreserveSig]
    int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}

/// <summary>
/// The one managed object behind the three callback interfaces: whatever the audio service reports (a session created, a session's
/// state changed or it disconnected, a default device changed) only wakes the monitor's thread, which re-reads the sessions. Nothing
/// here blocks, allocates beyond the call or lets an exception cross into the audio service. Volume and name changes are ignored.
/// </summary>
[GeneratedComClass]
internal sealed partial class SessionCallbacks : IAudioSessionNotification, IAudioSessionEvents, IMMNotificationClient
{
    private volatile Action<bool>? _wake;

    /// <param name="wake">Wakes the monitor; the argument is true when the default endpoints must be looked up again.</param>
    public SessionCallbacks(Action<bool> wake) => _wake = wake;

    /// <summary>Stops forwarding (the audio service may still call in while the registrations are released).</summary>
    public void Detach() => _wake = null;

    public int OnSessionCreated(nint newSession) => Wake(false);

    public int OnDisplayNameChanged(nint newDisplayName, nint eventContext) => 0;

    public int OnIconPathChanged(nint newIconPath, nint eventContext) => 0;

    public int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext) => 0;

    public int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel, nint eventContext) => 0;

    public int OnGroupingParamChanged(nint newGroupingParam, nint eventContext) => 0;

    public int OnStateChanged(int newState) => Wake(false);

    public int OnSessionDisconnected(int disconnectReason) => Wake(false);

    public int OnDeviceStateChanged(nint deviceId, uint newState) => Wake(true);

    public int OnDeviceAdded(nint deviceId) => 0;

    public int OnDeviceRemoved(nint deviceId) => Wake(true);

    public int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId) => Wake(true);

    public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => 0;

    private int Wake(bool endpoints)
    {
        try
        {
            _wake?.Invoke(endpoints);
        }
        catch (Exception)
        {
            // Never let an exception cross into the audio service's thread.
        }

        return 0;
    }
}
