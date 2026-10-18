using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Builds raw WAVEFORMATEX / WAVEFORMATEXTENSIBLE bytes exactly as mmreg.h lays them out (pack 1).</summary>
internal static class WaveFormatBytes
{
    public static byte[] Ex(ushort tag, int channels, int rate, int bits, int cbSize = 0, int? blockAlign = null)
    {
        var bytes = new byte[18 + cbSize];
        var align = blockAlign ?? channels * bits / 8;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)(rate * align));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), (ushort)cbSize);
        return bytes;
    }

    public static byte[] Extensible(Guid subFormat, int channels, int rate, int bits, int validBits, uint channelMask = 0x3, int cbSize = 22)
    {
        var bytes = Ex(WaveFormatParser.WaveFormatExtensible, channels, rate, bits, cbSize);
        if (cbSize >= 22)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)validBits);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), channelMask);
            subFormat.TryWriteBytes(bytes.AsSpan(24, 16));
        }

        return bytes;
    }

    public static MixFormat Float32(int channels = 2, int rate = 48_000) =>
        WaveFormatParser.Parse(Extensible(WaveFormatParser.KsDataFormatSubtypeIeeeFloat, channels, rate, 32, 32));

    public static MixFormat Pcm16(int channels = 2, int rate = 48_000) =>
        WaveFormatParser.Parse(Ex(WaveFormatParser.WaveFormatPcm, channels, rate, 16));
}

/// <summary>Records every sink call; also exposes the reconstructed timeline (silence as zeros).</summary>
internal sealed class RecordingSink(int channels) : IAudioSink
{
    public List<float> Samples { get; } = [];

    public List<(bool Silence, int Frames)> Calls { get; } = [];

    public long Frames => Samples.Count / channels;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        Assert.Equal(0, interleaved.Length % channels);
        Calls.Add((false, interleaved.Length / channels));
        foreach (var s in interleaved)
        {
            Samples.Add(s);
        }
    }

    public void WriteSilence(int frames)
    {
        Calls.Add((true, frames));
        for (var i = 0; i < frames * channels; i++)
        {
            Samples.Add(0f);
        }
    }
}

/// <summary>Allocation-free counting sink, safe to read after the capture thread was joined.</summary>
internal sealed class CountingSink(int channels) : IAudioSink
{
    private long _frames;
    private long _silenceFrames;
    private long _writes;
    private long _badLengths;

    public long Frames => Volatile.Read(ref _frames);

    public long SilenceFrames => Volatile.Read(ref _silenceFrames);

    public long Writes => Volatile.Read(ref _writes);

    public long BadLengths => Volatile.Read(ref _badLengths);

    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length % channels != 0)
        {
            Interlocked.Increment(ref _badLengths);
        }

        Interlocked.Increment(ref _writes);
        Interlocked.Add(ref _frames, interleaved.Length / channels);
    }

    public void WriteSilence(int frames)
    {
        Interlocked.Add(ref _frames, frames);
        Interlocked.Add(ref _silenceFrames, frames);
    }
}

/// <summary>
/// Managed stand-in for the WASAPI capture client: a preallocated ring of packets in native memory, so a drain of
/// queued packets allocates nothing. GetBuffer returns AUDCLNT_S_BUFFER_EMPTY when the queue is empty.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class FakeCaptureClient : IAudioCaptureClient, IDisposable
{
    private readonly Packet[] _packets;
    private readonly byte* _memory;
    private readonly int _maxPacketBytes;
    private int _head;
    private int _count;
    private bool _outstanding;

    public FakeCaptureClient(int capacity, int maxPacketBytes)
    {
        _packets = new Packet[capacity];
        _maxPacketBytes = maxPacketBytes;
        _memory = (byte*)NativeMemory.AllocZeroed((nuint)(capacity * maxPacketBytes));
    }

    public int GetBufferResult { get; set; }

    public int ReleaseBufferResult { get; set; }

    public int ReleaseCalls { get; private set; }

    public uint LastReleasedFrames { get; private set; }

    public int Queued => _count;

    public void Enqueue(ReadOnlySpan<byte> data, int frames, uint flags, long qpc)
    {
        if (_count == _packets.Length || data.Length > _maxPacketBytes)
        {
            throw new InvalidOperationException("Fake capture client is full.");
        }

        var slot = (_head + _count) % _packets.Length;
        data.CopyTo(new Span<byte>(_memory + (slot * _maxPacketBytes), _maxPacketBytes));
        _packets[slot] = new Packet(frames, flags, qpc);
        _count++;
    }

    public int GetBuffer(out nint data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition)
    {
        if (_outstanding)
        {
            throw new InvalidOperationException("GetBuffer called twice without ReleaseBuffer.");
        }

        data = 0;
        framesToRead = 0;
        flags = 0;
        devicePosition = 0;
        qpcPosition = 0;
        if (GetBufferResult != 0)
        {
            return GetBufferResult;
        }

        if (_count == 0)
        {
            return WasapiConstants.AudclntSBufferEmpty;
        }

        var packet = _packets[_head];
        data = (nint)(_memory + (_head * _maxPacketBytes));
        framesToRead = (uint)packet.Frames;
        flags = packet.Flags;
        qpcPosition = (ulong)packet.Qpc;
        _outstanding = true;
        return 0;
    }

    public int ReleaseBuffer(uint framesRead)
    {
        if (!_outstanding)
        {
            throw new InvalidOperationException("ReleaseBuffer without a packet.");
        }

        _outstanding = false;
        ReleaseCalls++;
        LastReleasedFrames = framesRead;
        _head = (_head + 1) % _packets.Length;
        _count--;
        return ReleaseBufferResult;
    }

    public int GetNextPacketSize(out uint framesInNextPacket)
    {
        framesInNextPacket = _count == 0 ? 0u : (uint)_packets[_head].Frames;
        return 0;
    }

    public void Dispose() => NativeMemory.Free(_memory);

    private readonly record struct Packet(int Frames, uint Flags, long Qpc);
}

/// <summary>Converts frame positions to 100 ns ticks so that <c>CaptureClock.FramesAt</c> maps back exactly.</summary>
internal static class Ticks
{
    public static long At(long frames, int rate, long start = 0) =>
        start + ((frames * CaptureClock.TicksPerSecond) + rate - 1) / rate;
}
