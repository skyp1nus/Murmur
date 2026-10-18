using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Audio.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Struct layouts, GUIDs, constants and vtable slots of the hand-written interop, checked against values copied
/// from the Windows SDK 10.0.26100 headers. The vtable tests expose a managed implementation through
/// <see cref="StrategyBasedComWrappers"/> and call raw slot N: the method that answers must be the one the
/// header's C "...Vtbl" struct lists at slot N. Runs on any OS (ComWrappers is cross-platform).
/// </summary>
public sealed unsafe class InteropLayoutTests
{
    [Fact]
    public void PropVariant_MatchesPropIdlLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Unsafe.SizeOf<PropVariant>());
        Assert.Equal(0, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.VarType)));
        Assert.Equal(8, (int)Marshal.OffsetOf<PropVariant>(nameof(PropVariant.Value)));
    }

    [Fact]
    public void PropertyKey_MatchesWtypesLayout()
    {
        Assert.Equal(20, Unsafe.SizeOf<PropertyKey>());
        Assert.Equal(16, (int)Marshal.OffsetOf<PropertyKey>(nameof(PropertyKey.PropertyId)));
    }

    [Fact]
    public void FriendlyNameKey_MatchesFunctionDiscoveryKeys()
    {
        var key = WasapiConstants.PkeyDeviceFriendlyName;
        Assert.Equal(new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), key.FormatId);
        Assert.Equal(14u, key.PropertyId);
    }

    [Theory]
    [InlineData(typeof(IMMDeviceEnumerator), "A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InlineData(typeof(IMMDeviceCollection), "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InlineData(typeof(IMMDevice), "D666063F-1587-4E43-81F1-B948E807363F")]
    [InlineData(typeof(IPropertyStore), "886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InlineData(typeof(IAudioClient), "1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InlineData(typeof(IAudioCaptureClient), "C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    public void InterfaceIids_MatchHeaders(Type type, string iid) =>
        Assert.Equal(new Guid(iid), type.GUID);

    [Fact]
    public void IidConstants_MatchInterfaces()
    {
        Assert.Equal(typeof(IMMDeviceEnumerator).GUID, WasapiConstants.IidIMMDeviceEnumerator);
        Assert.Equal(typeof(IAudioClient).GUID, WasapiConstants.IidIAudioClient);
        Assert.Equal(typeof(IAudioCaptureClient).GUID, WasapiConstants.IidIAudioCaptureClient);
        Assert.Equal(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), WasapiConstants.ClsidMMDeviceEnumerator);
    }

    [Fact]
    public void Hresults_MatchMakeHresult()
    {
        const int FacilityAudclnt = 2185; // winerror.h
        static int Err(int n) => unchecked((int)(0x80000000u | (FacilityAudclnt << 16) | (uint)n));

        int[] actual =
        [
            WasapiConstants.AudclntSBufferEmpty,
            WasapiConstants.AudclntEDeviceInvalidated,
            WasapiConstants.AudclntEServiceNotRunning,
            WasapiConstants.AudclntEResourcesInvalidated,
            WasapiConstants.ENotFound,
        ];

        Assert.Equal([(FacilityAudclnt << 16) | 1, Err(0x004), Err(0x010), Err(0x026), unchecked((int)(0x80070000u | 1168))], actual);
    }

    [Fact]
    public void Flags_MatchHeaders()
    {
        Assert.Equal(0x00020000u, WasapiConstants.AudclntStreamFlagsLoopback);
        Assert.Equal(0x00040000u, WasapiConstants.AudclntStreamFlagsEventCallback);
        Assert.Equal(0x17u, WasapiConstants.ClsctxAll);
        Assert.Equal(1u, WasapiConstants.AudclntBufferFlagsDataDiscontinuity);
        Assert.Equal(2u, WasapiConstants.AudclntBufferFlagsSilent);
        Assert.Equal(4u, WasapiConstants.AudclntBufferFlagsTimestampError);
    }

    private static nint Expose<T>(object implementation)
    {
        var wrappers = new StrategyBasedComWrappers();
        var unknown = wrappers.GetOrCreateComInterfaceForObject(implementation, CreateComInterfaceFlags.None);
        try
        {
            var iid = typeof(T).GUID;
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out var itf));
            return itf;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static void** Vtable(nint itf) => *(void***)itf;

    [Fact]
    public void AudioCaptureClient_VtableSlots()
    {
        var itf = Expose<IAudioCaptureClient>(new SlotAudioCaptureClient());
        try
        {
            var vt = Vtable(itf);
            nint data;
            uint frames, flags;
            ulong dev, qpc;
            Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, nint*, uint*, uint*, ulong*, ulong*, int>)vt[3])(itf, &data, &frames, &flags, &dev, &qpc));
            Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, uint, int>)vt[4])(itf, 0));
            Assert.Equal(5, ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)vt[5])(itf, &frames));
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    [Fact]
    public void AudioClient_VtableSlots()
    {
        var itf = Expose<IAudioClient>(new SlotAudioClient());
        try
        {
            var vt = Vtable(itf);
            uint u;
            long a, b;
            nint p;
            var g = Guid.Empty;
            Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, int, uint, long, long, nint, nint, int>)vt[3])(itf, 0, 0, 0, 0, 0, 0));
            Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)vt[4])(itf, &u));
            Assert.Equal(5, ((delegate* unmanaged[MemberFunction]<nint, long*, int>)vt[5])(itf, &a));
            Assert.Equal(6, ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)vt[6])(itf, &u));
            Assert.Equal(7, ((delegate* unmanaged[MemberFunction]<nint, int, nint, nint*, int>)vt[7])(itf, 0, 0, &p));
            Assert.Equal(8, ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)vt[8])(itf, &p));
            Assert.Equal(9, ((delegate* unmanaged[MemberFunction]<nint, long*, long*, int>)vt[9])(itf, &a, &b));
            Assert.Equal(10, ((delegate* unmanaged[MemberFunction]<nint, int>)vt[10])(itf));
            Assert.Equal(11, ((delegate* unmanaged[MemberFunction]<nint, int>)vt[11])(itf));
            Assert.Equal(12, ((delegate* unmanaged[MemberFunction]<nint, int>)vt[12])(itf));
            Assert.Equal(13, ((delegate* unmanaged[MemberFunction]<nint, nint, int>)vt[13])(itf, 0));
            Assert.Equal(14, ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)vt[14])(itf, &g, &p));
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    [Fact]
    public void MMDeviceEnumerator_VtableSlots()
    {
        var itf = Expose<IMMDeviceEnumerator>(new SlotDeviceEnumerator());
        try
        {
            var vt = Vtable(itf);
            nint p;
            var id = "x\0";
            fixed (char* pId = id)
            {
                Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, int, uint, nint*, int>)vt[3])(itf, 0, 1, &p));
                Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, int, int, nint*, int>)vt[4])(itf, 0, 0, &p));
                Assert.Equal(5, ((delegate* unmanaged[MemberFunction]<nint, char*, nint*, int>)vt[5])(itf, pId, &p));
            }

            Assert.Equal(6, ((delegate* unmanaged[MemberFunction]<nint, nint, int>)vt[6])(itf, 0));
            Assert.Equal(7, ((delegate* unmanaged[MemberFunction]<nint, nint, int>)vt[7])(itf, 0));
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    [Fact]
    public void MMDeviceCollection_And_MMDevice_VtableSlots()
    {
        var collection = Expose<IMMDeviceCollection>(new SlotDeviceCollection());
        var device = Expose<IMMDevice>(new SlotDevice());
        try
        {
            uint u;
            nint p;
            var g = Guid.Empty;
            var vc = Vtable(collection);
            Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)vc[3])(collection, &u));
            Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)vc[4])(collection, 0, &p));

            var vd = Vtable(device);
            Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, Guid*, uint, nint, nint*, int>)vd[3])(device, &g, 0, 0, &p));
            Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)vd[4])(device, 0, &p));
            Assert.Equal(5, ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)vd[5])(device, &p));
            Assert.Equal(6, ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)vd[6])(device, &u));
        }
        finally
        {
            Marshal.Release(collection);
            Marshal.Release(device);
        }
    }

    [Fact]
    public void PropertyStore_VtableSlots()
    {
        var itf = Expose<IPropertyStore>(new SlotPropertyStore());
        try
        {
            var vt = Vtable(itf);
            uint u;
            PropertyKey key;
            PropVariant value;
            Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)vt[3])(itf, &u));
            Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, uint, PropertyKey*, int>)vt[4])(itf, 0, &key));
            Assert.Equal(5, ((delegate* unmanaged[MemberFunction]<nint, PropertyKey*, PropVariant*, int>)vt[5])(itf, &key, &value));
            Assert.Equal(6, ((delegate* unmanaged[MemberFunction]<nint, PropertyKey*, PropVariant*, int>)vt[6])(itf, &key, &value));
            Assert.Equal(7, ((delegate* unmanaged[MemberFunction]<nint, int>)vt[7])(itf));
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    [Fact]
    public void PropertyStore_GetValue_RoundTripsThroughVtable()
    {
        var itf = Expose<IPropertyStore>(new SlotPropertyStore());
        try
        {
            var key = WasapiConstants.PkeyDeviceFriendlyName;
            PropVariant value;
            Assert.Equal(5, ((delegate* unmanaged[MemberFunction]<nint, PropertyKey*, PropVariant*, int>)Vtable(itf)[5])(itf, &key, &value));
            Assert.Equal(WasapiConstants.VtLpwstr, value.VarType);
            Assert.Equal((nint)14, value.Value); // the fake echoes key.PropertyId
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    /// <summary>
    /// The production path calls IAudioCaptureClient through a source-generated RCW. Drain a stream through a real
    /// RCW (wrapping a CCW of the fake) and assert the generated stubs + ComObject lookups allocate nothing.
    /// </summary>
    [Fact]
    public void Drain_ThroughGeneratedRcw_DoesNotAllocate()
    {
        const int Packet = 480;
        using var fake = new FakeCaptureClient(64, Packet * 8);
        var itf = Expose<IAudioCaptureClient>(fake);
        var rcw = (IAudioCaptureClient)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(itf, CreateObjectFlags.UniqueInstance);
        Marshal.Release(itf);
        try
        {
            var pump = new CapturePump(WaveFormatBytes.Float32(2), Packet, 1_920);
            var sink = new CountingSink(2);
            var packet = new byte[Packet * 8];
            pump.Start(sink, 1_000_000); // QPC position 0 means "no timestamp", so start later
            long frame = 0;
            var failures = 0;
            void Round()
            {
                for (var i = 0; i < 32; i++)
                {
                    fake.Enqueue(packet, Packet, 0, Ticks.At(frame, 48_000, 1_000_000));
                    frame += Packet;
                }

                if (pump.Drain(rcw, Ticks.At(frame, 48_000, 1_000_000)) != 0)
                {
                    failures++;
                }
            }

            Round();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < 100; r++)
            {
                Round();
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(0, failures);
            Assert.Equal(frame, sink.Frames);
        }
        finally
        {
            ComHelpers.Release(rcw);
        }
    }
}

[GeneratedComClass]
internal sealed partial class SlotAudioCaptureClient : IAudioCaptureClient
{
    public int GetBuffer(out nint data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition)
    {
        (data, framesToRead, flags, devicePosition, qpcPosition) = (0, 0, 0, 0, 0);
        return 3;
    }

    public int ReleaseBuffer(uint framesRead) => 4;

    public int GetNextPacketSize(out uint framesInNextPacket)
    {
        framesInNextPacket = 0;
        return 5;
    }
}

[GeneratedComClass]
internal sealed partial class SlotAudioClient : IAudioClient
{
    public int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid) => 3;

    public int GetBufferSize(out uint bufferFrames)
    {
        bufferFrames = 0;
        return 4;
    }

    public int GetStreamLatency(out long latency)
    {
        latency = 0;
        return 5;
    }

    public int GetCurrentPadding(out uint paddingFrames)
    {
        paddingFrames = 0;
        return 6;
    }

    public int IsFormatSupported(int shareMode, nint format, out nint closestMatch)
    {
        closestMatch = 0;
        return 7;
    }

    public int GetMixFormat(out nint deviceFormat)
    {
        deviceFormat = 0;
        return 8;
    }

    public int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod)
    {
        (defaultDevicePeriod, minimumDevicePeriod) = (0, 0);
        return 9;
    }

    public int Start() => 10;

    public int Stop() => 11;

    public int Reset() => 12;

    public int SetEventHandle(nint eventHandle) => 13;

    public int GetService(in Guid riid, out nint service)
    {
        service = 0;
        return 14;
    }
}

[GeneratedComClass]
internal sealed partial class SlotDeviceEnumerator : IMMDeviceEnumerator
{
    public int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices)
    {
        devices = 0;
        return 3;
    }

    public int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint)
    {
        endpoint = 0;
        return 4;
    }

    public int GetDevice(string id, out nint device)
    {
        device = 0;
        return id == "x" ? 5 : -5;
    }

    public int RegisterEndpointNotificationCallback(nint client) => 6;

    public int UnregisterEndpointNotificationCallback(nint client) => 7;
}

[GeneratedComClass]
internal sealed partial class SlotDeviceCollection : IMMDeviceCollection
{
    public int GetCount(out uint count)
    {
        count = 0;
        return 3;
    }

    public int Item(uint index, out nint device)
    {
        device = 0;
        return 4;
    }
}

[GeneratedComClass]
internal sealed partial class SlotDevice : IMMDevice
{
    public int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint ppInterface)
    {
        ppInterface = 0;
        return 3;
    }

    public int OpenPropertyStore(uint stgmAccess, out nint properties)
    {
        properties = 0;
        return 4;
    }

    public int GetId(out nint id)
    {
        id = 0;
        return 5;
    }

    public int GetState(out uint state)
    {
        state = 0;
        return 6;
    }
}

[GeneratedComClass]
internal sealed partial class SlotPropertyStore : IPropertyStore
{
    public int GetCount(out uint count)
    {
        count = 0;
        return 3;
    }

    public int GetAt(uint index, out PropertyKey key)
    {
        key = default;
        return 4;
    }

    public int GetValue(in PropertyKey key, out PropVariant value)
    {
        value = new PropVariant { VarType = WasapiConstants.VtLpwstr, Value = (nint)key.PropertyId };
        return 5;
    }

    public int SetValue(in PropertyKey key, in PropVariant value) => 6;

    public int Commit() => 7;
}
