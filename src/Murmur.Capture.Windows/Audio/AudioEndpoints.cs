using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Audio;

/// <summary>An opened endpoint: its description and a copy of its shared-mode mix format (raw WAVEFORMATEX bytes).</summary>
internal sealed record EndpointInfo(AudioDevice Device, byte[] MixFormatBytes, MixFormat MixFormat);

/// <summary>
/// MMDevice API queries: endpoint enumeration, default endpoints, friendly names and mix formats.
/// Each call joins the MTA for its own duration and releases every COM object before returning.
/// </summary>
internal static unsafe class AudioEndpoints
{
    public static int DataFlow(AudioDeviceKind kind) =>
        kind == AudioDeviceKind.Microphone ? WasapiConstants.ECapture : WasapiConstants.ERender;

    public static bool HasActive(AudioDeviceKind kind)
    {
        try
        {
            using var apartment = ComHelpers.EnterMta();
            var enumerator = CreateEnumerator();
            try
            {
                var collection = OpenCollection(enumerator, kind);
                try
                {
                    ComHelpers.Check(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");
                    return count > 0;
                }
                finally
                {
                    ComHelpers.Release(collection);
                }
            }
            finally
            {
                ComHelpers.Release(enumerator);
            }
        }
        catch (COMException)
        {
            return false; // e.g. the audio service is not running: report "no device" rather than crash
        }
    }

    public static IReadOnlyList<AudioDevice> Enumerate(AudioDeviceKind kind)
    {
        using var apartment = ComHelpers.EnterMta();
        var enumerator = CreateEnumerator();
        try
        {
            var defaultId = GetDefaultId(enumerator, kind);
            var collection = OpenCollection(enumerator, kind);
            try
            {
                ComHelpers.Check(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");
                var devices = new List<AudioDevice>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    ComHelpers.Check(collection.Item(i, out var pDevice), "IMMDeviceCollection.Item");
                    var device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceCollection.Item");
                    try
                    {
                        var id = ReadId(device);
                        devices.Add(new AudioDevice(id, ReadFriendlyName(device, id), kind, string.Equals(id, defaultId, StringComparison.Ordinal)));
                    }
                    finally
                    {
                        ComHelpers.Release(device);
                    }
                }

                return devices;
            }
            finally
            {
                ComHelpers.Release(collection);
            }
        }
        finally
        {
            ComHelpers.Release(enumerator);
        }
    }

    /// <summary>Resolves <paramref name="deviceId"/> (null = default endpoint) and reads its name and mix format.</summary>
    public static EndpointInfo Open(AudioDeviceKind kind, string? deviceId)
    {
        using var apartment = ComHelpers.EnterMta();
        var enumerator = CreateEnumerator();
        try
        {
            var defaultId = GetDefaultId(enumerator, kind);
            var id = deviceId ?? defaultId
                ?? throw new InvalidOperationException(kind == AudioDeviceKind.Microphone
                    ? "No default microphone (capture endpoint) is available."
                    : "No default output (render endpoint) is available for loopback capture.");

            ComHelpers.Check(enumerator.GetDevice(id, out var pDevice), $"IMMDeviceEnumerator.GetDevice({id})");
            var device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceEnumerator.GetDevice");
            try
            {
                ComHelpers.Check(device.GetState(out var state), "IMMDevice.GetState");
                if ((state & WasapiConstants.DeviceStateActive) == 0)
                {
                    throw new InvalidOperationException($"Audio endpoint {id} is not active (state 0x{state:X}).");
                }

                var name = ReadFriendlyName(device, id);
                var mixFormatBytes = ReadMixFormat(device);
                var mixFormat = WaveFormatParser.Parse(mixFormatBytes);
                var info = new AudioDevice(id, name, kind, string.Equals(id, defaultId, StringComparison.Ordinal));
                return new EndpointInfo(info, mixFormatBytes, mixFormat);
            }
            finally
            {
                ComHelpers.Release(device);
            }
        }
        finally
        {
            ComHelpers.Release(enumerator);
        }
    }

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        ComHelpers.Check(
            NativeMethods.CoCreateInstance(
                in WasapiConstants.ClsidMMDeviceEnumerator, 0, WasapiConstants.ClsctxAll, in WasapiConstants.IidIMMDeviceEnumerator, out var pEnumerator),
            "CoCreateInstance(MMDeviceEnumerator)");
        return ComHelpers.Wrap<IMMDeviceEnumerator>(pEnumerator, "CoCreateInstance(MMDeviceEnumerator)");
    }

    private static IMMDeviceCollection OpenCollection(IMMDeviceEnumerator enumerator, AudioDeviceKind kind)
    {
        ComHelpers.Check(
            enumerator.EnumAudioEndpoints(DataFlow(kind), WasapiConstants.DeviceStateActive, out var pCollection),
            "IMMDeviceEnumerator.EnumAudioEndpoints");
        return ComHelpers.Wrap<IMMDeviceCollection>(pCollection, "IMMDeviceEnumerator.EnumAudioEndpoints");
    }

    private static string? GetDefaultId(IMMDeviceEnumerator enumerator, AudioDeviceKind kind)
    {
        var hr = enumerator.GetDefaultAudioEndpoint(DataFlow(kind), WasapiConstants.EConsole, out var pDevice);
        if (hr == WasapiConstants.ENotFound)
        {
            return null; // no endpoint of this kind
        }

        ComHelpers.Check(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
        var device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
        try
        {
            return ReadId(device);
        }
        finally
        {
            ComHelpers.Release(device);
        }
    }

    private static string ReadId(IMMDevice device)
    {
        ComHelpers.Check(device.GetId(out var pId), "IMMDevice.GetId");
        try
        {
            return Marshal.PtrToStringUni(pId) ?? string.Empty;
        }
        finally
        {
            NativeMethods.CoTaskMemFree(pId);
        }
    }

    private static string ReadFriendlyName(IMMDevice device, string fallback)
    {
        if (device.OpenPropertyStore(WasapiConstants.StgmRead, out var pStore) < 0 || pStore == 0)
        {
            return fallback;
        }

        var store = ComHelpers.Wrap<IPropertyStore>(pStore, "IMMDevice.OpenPropertyStore");
        try
        {
            if (store.GetValue(in WasapiConstants.PkeyDeviceFriendlyName, out var value) < 0)
            {
                return fallback;
            }

            try
            {
                return value.VarType == WasapiConstants.VtLpwstr && value.Value != 0
                    ? Marshal.PtrToStringUni(value.Value) ?? fallback
                    : fallback;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            ComHelpers.Release(store);
        }
    }

    private static byte[] ReadMixFormat(IMMDevice device)
    {
        ComHelpers.Check(device.Activate(in WasapiConstants.IidIAudioClient, WasapiConstants.ClsctxAll, 0, out var pClient), "IMMDevice.Activate(IAudioClient)");
        var client = ComHelpers.Wrap<IAudioClient>(pClient, "IMMDevice.Activate(IAudioClient)");
        try
        {
            ComHelpers.Check(client.GetMixFormat(out var pFormat), "IAudioClient.GetMixFormat");
            try
            {
                var header = new ReadOnlySpan<byte>((void*)pFormat, WaveFormatParser.WaveFormatExSize);
                return new ReadOnlySpan<byte>((void*)pFormat, WaveFormatParser.GetTotalSize(header)).ToArray();
            }
            finally
            {
                NativeMethods.CoTaskMemFree(pFormat);
            }
        }
        finally
        {
            ComHelpers.Release(client);
        }
    }
}
