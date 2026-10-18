using System.Buffers.Binary;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Playback;
using Murmur.Core.Audio;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Builds small WAV files / streams for the playback tests.</summary>
internal static class TestWav
{
    /// <summary>Canonical PCM16 WAV; <paramref name="declaredDataBytes"/> overrides the data chunk size (stale header).</summary>
    public static byte[] Pcm16(ReadOnlySpan<short> samples, int sampleRate = 16_000, int channels = 1, long? declaredDataBytes = null)
    {
        var bytes = new byte[WavPcm16Writer.HeaderSize + (samples.Length * 2)];
        WavPcm16Writer.WriteHeader(bytes, sampleRate, channels, declaredDataBytes ?? samples.Length * 2L);
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(WavPcm16Writer.HeaderSize + (i * 2)), samples[i]);
        }

        return bytes;
    }

    /// <summary>A 44-byte-header WAV with an arbitrary format tag / bit depth and zeroed data.</summary>
    public static byte[] Raw(ushort tag, int channels, int sampleRate, int bits, int frames)
    {
        var blockAlign = channels * bits / 8;
        var bytes = Pcm16([], sampleRate, channels);
        Array.Resize(ref bytes, WavPcm16Writer.HeaderSize + (frames * blockAlign));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)(frames * blockAlign));
        return bytes;
    }

    public static Pcm16TrackReader Reader(ReadOnlySpan<short> samples, int sampleRate = 16_000) =>
        Pcm16TrackReader.Open(new MemoryStream(Pcm16(samples, sampleRate)));

    public static short[] Ramp(int length, int start = 0, int step = 1)
    {
        var samples = new short[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = (short)(start + (i * step));
        }

        return samples;
    }

    public static short[] Constant(int length, short value)
    {
        var samples = new short[length];
        samples.AsSpan().Fill(value);
        return samples;
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murmur-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string name, byte[] bytes)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Scriptable <see cref="IPlaybackEngine"/>: records commands, models a stream as "position = mixer position" (no
/// device buffer), and lets a test fail commands or fire the owner's callbacks.
/// </summary>
internal sealed class FakeEngine : IPlaybackEngine
{
    private long _generation;

    public FakeEngine(TrackMixer mixer, long startFrame, IPlaybackEngineOwner owner)
    {
        Mixer = mixer;
        Owner = owner;
        Mixer.Seek(startFrame);
        StartFrame = Mixer.Position;
        PlayedFrame = StartFrame;
    }

    public TrackMixer Mixer { get; }

    public IPlaybackEngineOwner Owner { get; }

    public long StartFrame { get; }

    public List<string> Commands { get; } = [];

    public bool Running { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>When negative, the next command returns it.</summary>
    public int FailNext { get; set; }

    public long PlayedFrame { get; set; }

    public long Generation => Interlocked.Read(ref _generation);

    public int Play()
    {
        Record("play");
        if (FailNext < 0)
        {
            return TakeFailure();
        }

        Running = true;
        return 0;
    }

    public int Pause()
    {
        Record("pause");
        if (FailNext < 0)
        {
            return TakeFailure();
        }

        Running = false;
        return 0;
    }

    public int Seek(long frame, bool play)
    {
        Record(play ? $"seek:{frame}:play" : $"seek:{frame}");
        if (FailNext < 0)
        {
            return TakeFailure();
        }

        Mixer.Seek(frame);
        PlayedFrame = Mixer.Position;
        Running = play;
        return 0;
    }

    /// <summary>Simulates the render thread reaching the end.</summary>
    public void FireEnded()
    {
        Running = false;
        PlayedFrame = Mixer.TotalFrames;
        Owner.OnEnded(this, Generation);
    }

    public void FireFault() => Owner.OnFault(this, WasapiConstants.AudclntEDeviceInvalidated, ComHelpers.Describe(WasapiConstants.AudclntEDeviceInvalidated));

    public void Dispose()
    {
        if (!Disposed)
        {
            Disposed = true;
            Mixer.Dispose();
        }
    }

    private void Record(string command)
    {
        Assert.False(Disposed, "Command sent to a disposed engine.");
        Interlocked.Increment(ref _generation);
        Commands.Add(command);
    }

    private int TakeFailure()
    {
        var hr = FailNext;
        FailNext = 0;
        return hr;
    }
}

/// <summary>Creates <see cref="FakeEngine"/>s and keeps them; can be told to fail creation.</summary>
internal sealed class FakeEngineFactory
{
    public List<FakeEngine> Engines { get; } = [];

    public Exception? FailWith { get; set; }

    /// <summary>Copied to <see cref="FakeEngine.FailNext"/> of every engine created from now on.</summary>
    public int NewEngineFailNext { get; set; }

    public FakeEngine Last => Engines[^1];

    public IPlaybackEngine Create(TrackMixer mixer, long startFrame, IPlaybackEngineOwner owner)
    {
        if (FailWith is { } error)
        {
            throw error;
        }

        var engine = new FakeEngine(mixer, startFrame, owner) { FailNext = NewEngineFailNext };
        Engines.Add(engine);
        return engine;
    }
}
