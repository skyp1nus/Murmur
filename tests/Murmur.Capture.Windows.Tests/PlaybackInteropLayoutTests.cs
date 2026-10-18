using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Playback;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The render-side interop added for playback, checked against the Windows SDK 10.0.26100 headers the same way as
/// <see cref="InteropLayoutTests"/>: IAudioRenderClient (Audioclient.h, IAudioRenderClientVtbl: GetBuffer = slot 3,
/// ReleaseBuffer = slot 4) and the AUDCLNT_STREAMFLAGS_* values (AudioSessionTypes.h).
/// </summary>
public sealed unsafe class PlaybackInteropLayoutTests
{
    [Fact]
    public void AudioRenderClient_Iid_MatchesAudioclientH()
    {
        Assert.Equal(new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), typeof(IAudioRenderClient).GUID);
        Assert.Equal(typeof(IAudioRenderClient).GUID, WasapiConstants.IidIAudioRenderClient);
    }

    [Fact]
    public void StreamFlags_MatchAudioSessionTypesH()
    {
        Assert.Equal(0x08000000u, WasapiConstants.AudclntStreamFlagsSrcDefaultQuality);
        Assert.Equal(0x80000000u, WasapiConstants.AudclntStreamFlagsAutoConvertPcm);
        Assert.Equal(0x88040000u, WasapiRenderEngine.StreamFlags);
        Assert.Equal(unchecked((int)0x80004005), WasapiConstants.EFail);
    }

    [Fact]
    public void AudioRenderClient_VtableSlots()
    {
        var itf = Expose<IAudioRenderClient>(new SlotAudioRenderClient());
        try
        {
            var vt = *(void***)itf;
            nint data;
            Assert.Equal(3, ((delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)vt[3])(itf, 160, &data));
            Assert.Equal((nint)160, data); // the fake echoes NumFramesRequested through ppData
            Assert.Equal(4, ((delegate* unmanaged[MemberFunction]<nint, uint, uint, int>)vt[4])(itf, 160, 0));
            Assert.Equal(-4, ((delegate* unmanaged[MemberFunction]<nint, uint, uint, int>)vt[4])(itf, 160, 2)); // flags reach dwFlags
        }
        finally
        {
            Marshal.Release(itf);
        }
    }

    [Fact]
    public void AudioRenderClient_ThroughGeneratedRcw_DoesNotAllocate()
    {
        var itf = Expose<IAudioRenderClient>(new SlotAudioRenderClient());
        var rcw = (IAudioRenderClient)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(itf, CreateObjectFlags.UniqueInstance);
        Marshal.Release(itf);
        try
        {
            rcw.GetBuffer(1, out _);
            rcw.ReleaseBuffer(1, 0);
            var failures = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
            {
                failures += rcw.GetBuffer(160, out var data) == 3 && data == 160 ? 0 : 1;
                failures += rcw.ReleaseBuffer(160, 0) == 4 ? 0 : 1;
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(0, failures);
        }
        finally
        {
            ComHelpers.Release(rcw);
        }
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
}

[GeneratedComClass]
internal sealed partial class SlotAudioRenderClient : IAudioRenderClient
{
    public int GetBuffer(uint framesRequested, out nint data)
    {
        data = (nint)framesRequested;
        return 3;
    }

    public int ReleaseBuffer(uint framesWritten, uint flags) => flags == 0 ? 4 : -4;
}
