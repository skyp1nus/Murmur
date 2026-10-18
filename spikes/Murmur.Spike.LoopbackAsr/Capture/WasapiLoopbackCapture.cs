using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Murmur.Spike.LoopbackAsr.Capture;

/// <summary>Result of a loopback capture: mono float at the device mix rate.</summary>
internal sealed record LoopbackRecording(float[] Mono, int SampleRate, string FormatDescription, long GapFrames, int SilentPackets, int Discontinuities);

/// <summary>
/// Captures N seconds from the default render endpoint in WASAPI shared-mode loopback (polling, ~10 ms).
/// UNVERIFIED-ON-WINDOWS: compiled on Linux only; vtables checked against the Windows SDK headers.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WasapiLoopbackCapture
{
    private const long OneSecond100ns = 10_000_000;

    public static LoopbackRecording Capture(double seconds)
    {
        var hr = WasapiNative.CoInitializeEx(0, WasapiNative.CoinitMultithreaded);
        // S_FALSE: already initialised on this thread; RPC_E_CHANGED_MODE: thread is STA - COM is still usable.
        var mustUninitialize = hr is WasapiNative.SOk or WasapiNative.SFalse;
        if (hr < 0 && hr != WasapiNative.RpcEChangedMode)
        {
            Check(hr, "CoInitializeEx(COINIT_MULTITHREADED)");
        }

        var cw = new StrategyBasedComWrappers();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint mixFormat = 0;
        var started = false;
        try
        {
            Check(
                WasapiNative.CoCreateInstance(
                    in WasapiNative.ClsidMMDeviceEnumerator, 0, WasapiNative.ClsctxAll, in WasapiNative.IidIMMDeviceEnumerator, out var pEnum),
                "CoCreateInstance(MMDeviceEnumerator)");
            enumerator = Wrap<IMMDeviceEnumerator>(cw, pEnum);

            Check(enumerator.GetDefaultAudioEndpoint(WasapiNative.ERender, WasapiNative.EConsole, out var pDevice), "IMMDeviceEnumerator.GetDefaultAudioEndpoint(eRender, eConsole)");
            device = Wrap<IMMDevice>(cw, pDevice);

            Check(device.Activate(in WasapiNative.IidIAudioClient, WasapiNative.ClsctxAll, 0, out var pClient), "IMMDevice.Activate(IAudioClient)");
            client = Wrap<IAudioClient>(cw, pClient);

            Check(client.GetMixFormat(out mixFormat), "IAudioClient.GetMixFormat");
            var (sampleRate, channels, format, description) = DescribeMixFormat(mixFormat);

            Check(
                client.Initialize(WasapiNative.AudclntShareModeShared, WasapiNative.AudclntStreamFlagsLoopback, OneSecond100ns, 0, mixFormat, 0),
                "IAudioClient.Initialize(SHARED, LOOPBACK, 1 s)");

            // The mix format is no longer needed once the stream is initialised.
            WasapiNative.CoTaskMemFree(mixFormat);
            mixFormat = 0;

            Check(client.GetBufferSize(out var bufferFrames), "IAudioClient.GetBufferSize");
            Check(client.GetService(in WasapiNative.IidIAudioCaptureClient, out var pCapture), "IAudioClient.GetService(IAudioCaptureClient)");
            capture = Wrap<IAudioCaptureClient>(cw, pCapture);

            Console.WriteLine($"Loopback: {description}, endpoint buffer {bufferFrames} frames. Recording {seconds:0.#} s - play something with speech now.");

            // Allocation-free from here until Stop: preallocated timeline, no LINQ, no string formatting.
            var startTicks = Stopwatch.GetTimestamp();
            var timeline = new CaptureTimeline(sampleRate, channels, format, seconds, To100ns(startTicks));
            var durationTicks = (long)(seconds * Stopwatch.Frequency);

            Check(client.Start(), "IAudioClient.Start");
            started = true;

            while (Stopwatch.GetTimestamp() - startTicks < durationTicks && !timeline.IsFull)
            {
                Check(capture.GetNextPacketSize(out var packetFrames), "IAudioCaptureClient.GetNextPacketSize");
                while (packetFrames > 0)
                {
                    Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc), "IAudioCaptureClient.GetBuffer");

                    var silent = (flags & WasapiNative.AudclntBufferFlagsSilent) != 0;
                    var discontinuity = (flags & WasapiNative.AudclntBufferFlagsDataDiscontinuity) != 0;
                    var qpcValid = (flags & WasapiNative.AudclntBufferFlagsTimestampError) == 0;
                    var bytes = new ReadOnlySpan<byte>((void*)data, silent ? 0 : (int)frames * timeline.BytesPerFrame);
                    timeline.Append(bytes, (int)frames, silent, discontinuity, qpcValid ? (long)qpc : 0);

                    Check(capture.ReleaseBuffer(frames), "IAudioCaptureClient.ReleaseBuffer");
                    Check(capture.GetNextPacketSize(out packetFrames), "IAudioCaptureClient.GetNextPacketSize");
                }

                Thread.Sleep(10);
            }

            Check(client.Stop(), "IAudioClient.Stop");
            started = false;

            var mono = timeline.Complete();
            return new LoopbackRecording(mono, sampleRate, description, timeline.GapFrames, timeline.SilentPackets, timeline.Discontinuities);
        }
        finally
        {
            if (started)
            {
                client?.Stop();
            }

            if (mixFormat != 0)
            {
                WasapiNative.CoTaskMemFree(mixFormat);
            }

            Release(capture);
            Release(client);
            Release(device);
            Release(enumerator);
            if (mustUninitialize)
            {
                WasapiNative.CoUninitialize();
            }
        }
    }

    private static (int SampleRate, int Channels, PacketSampleFormat Format, string Description) DescribeMixFormat(nint pFormat)
    {
        var wfx = (WaveFormatEx*)pFormat;
        var tag = wfx->FormatTag;
        var bits = wfx->BitsPerSample;
        var subFormat = Guid.Empty;
        if (tag == WasapiNative.WaveFormatExtensible)
        {
            if (wfx->CbSize < sizeof(WaveFormatExtensible) - sizeof(WaveFormatEx))
            {
                throw new NotSupportedException($"WAVE_FORMAT_EXTENSIBLE with cbSize={wfx->CbSize} (< 22).");
            }

            subFormat = ((WaveFormatExtensible*)pFormat)->SubFormat;
        }

        var isFloat = tag == WasapiNative.WaveFormatIeeeFloat || (tag == WasapiNative.WaveFormatExtensible && subFormat == WasapiNative.KsDataFormatSubtypeIeeeFloat);
        var isPcm = tag == WasapiNative.WaveFormatPcm || (tag == WasapiNative.WaveFormatExtensible && subFormat == WasapiNative.KsDataFormatSubtypePcm);

        PacketSampleFormat format;
        if (isFloat && bits == 32)
        {
            format = PacketSampleFormat.Float32;
        }
        else if (isPcm && bits == 16)
        {
            format = PacketSampleFormat.Pcm16;
        }
        else
        {
            throw new NotSupportedException($"Unsupported mix format: tag=0x{tag:X4}, subformat={subFormat}, bits={bits}.");
        }

        var expectedBlockAlign = wfx->Channels * (bits / 8);
        if (wfx->BlockAlign != expectedBlockAlign)
        {
            throw new NotSupportedException($"Unexpected nBlockAlign={wfx->BlockAlign} for {wfx->Channels} ch x {bits} bit.");
        }

        var description = $"{wfx->SamplesPerSec} Hz, {wfx->Channels} ch, {format}{(tag == WasapiNative.WaveFormatExtensible ? " (WAVE_FORMAT_EXTENSIBLE)" : "")}";
        return ((int)wfx->SamplesPerSec, wfx->Channels, format, description);
    }

    private static T Wrap<T>(StrategyBasedComWrappers cw, nint p)
        where T : class
    {
        if (p == 0)
        {
            throw new COMException($"Null {typeof(T).Name} pointer.");
        }

        try
        {
            // UniqueInstance: the RCW owns its own reference and can be released with ComObject.FinalRelease().
            return (T)cw.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p); // drop the reference returned by the out-parameter; the RCW holds its own.
        }
    }

    private static void Release(object? rcw)
    {
        if (rcw is ComObject com)
        {
            com.FinalRelease();
        }
    }

    private static long To100ns(long stopwatchTicks) =>
        (long)((Int128)stopwatchTicks * OneSecond100ns / Stopwatch.Frequency);

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"{what} failed: HRESULT 0x{hr:X8}", hr);
        }
    }
}
