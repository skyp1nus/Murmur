using Murmur.Core.Audio;
using Murmur.Core.Import;
using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Fakes;

/// <summary>In-memory <see cref="IImportStore"/> (rows keyed by id, oldest first).</summary>
internal sealed class InMemoryImportStore : IImportStore
{
    private readonly SortedDictionary<long, StoredImport> _rows = [];
    private long _next;

    public int Saves { get; private set; }

    /// <summary>When set and returning an exception for an entry, <see cref="SaveImportAsync"/> throws it.</summary>
    public Func<StoredImport, Exception?>? FailSave { get; set; }

    /// <summary>Deletes the entries of a session deleted from <paramref name="sessions"/> (the real store's ON DELETE CASCADE).</summary>
    public InMemoryImportStore CascadeFrom(InMemorySessionStore sessions)
    {
        sessions.Deleted += id =>
        {
            lock (_rows)
            {
                foreach (var row in _rows.Values.Where(r => r.Item.Session == id).ToList())
                {
                    _rows.Remove(row.Item.Id);
                }
            }
        };
        return this;
    }

    public IReadOnlyList<StoredImport> Rows
    {
        get
        {
            lock (_rows)
            {
                return [.. _rows.Values];
            }
        }
    }

    public void Seed(StoredImport import)
    {
        lock (_rows)
        {
            _rows[import.Item.Id] = import;
            _next = Math.Max(_next, import.Item.Id);
        }
    }

    public ValueTask<ImportItem> SaveImportAsync(StoredImport import, CancellationToken cancellationToken)
    {
        if (FailSave?.Invoke(import) is { } error)
        {
            throw error;
        }

        lock (_rows)
        {
            Saves++;
            if (import.Item.Id == 0)
            {
                var item = import.Item with { Id = ++_next };
                _rows[item.Id] = import with { Item = item };
                return ValueTask.FromResult(item);
            }

            if (_rows.TryGetValue(import.Item.Id, out var stored))
            {
                _rows[import.Item.Id] = stored with { Item = import.Item };
            }

            return ValueTask.FromResult(import.Item);
        }
    }

    public ValueTask<IReadOnlyList<StoredImport>> LoadImportsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Rows);

    public ValueTask DeleteImportAsync(long id, CancellationToken cancellationToken)
    {
        lock (_rows)
        {
            _rows.Remove(id);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Scriptable <see cref="IMediaDecoder"/>: probes return <see cref="Kind"/>/<see cref="Duration"/> (or throw
/// <see cref="ProbeError"/> for a file name in <see cref="FailOn"/>), decodes write a 16 kHz mono square-wave WAV of
/// <see cref="Duration"/>, optionally waiting for <see cref="Gate"/> (cancellable) after reporting half the progress.
/// </summary>
internal sealed class FakeMediaDecoder(params string[] extensions) : IMediaDecoder
{
    private readonly List<string> _decoded = [];

    public IReadOnlySet<string> Extensions { get; } = new HashSet<string>(extensions, StringComparer.Ordinal);

    public MediaKind Kind { get; set; } = MediaKind.Audio;

    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>File names whose probe throws <see cref="ProbeError"/>.</summary>
    public HashSet<string> FailOn { get; } = new(StringComparer.Ordinal);

    public Func<Exception> ProbeError { get; set; } = () => new InvalidDataException("damaged");

    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Set when a decode reached the gate (or started, without one).</summary>
    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Probes { get; private set; }

    /// <summary>Source paths of every decode that started.</summary>
    public IReadOnlyList<string> Decoded
    {
        get
        {
            lock (_decoded)
            {
                return [.. _decoded];
            }
        }
    }

    public void ResetStarted() => Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        Probes++;
        if (FailOn.Contains(Path.GetFileName(path)))
        {
            throw ProbeError();
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("gone", path);
        }

        return ValueTask.FromResult(new MediaProbe(path, Kind, new FileInfo(path).Length, Duration));
    }

    public async ValueTask DecodeAudioAsync(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        lock (_decoded)
        {
            _decoded.Add(path);
        }

        progress?.Report(0.5);
        Started.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        TestWav.Square(destinationWav, Duration, 0.5f);
        progress?.Report(1);
    }
}

/// <summary>Writes WAVs of any format for the decoder tests.</summary>
internal static class AnyWav
{
    /// <summary>Interleaved float samples as 32-bit IEEE-float WAV.</summary>
    public static string Float32(string path, int sampleRate, int channels, ReadOnlySpan<float> interleaved)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = interleaved.Length * 4;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)3);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 4);
        writer.Write((short)(channels * 4));
        writer.Write((short)32);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in interleaved)
        {
            writer.Write(sample);
        }

        return path;
    }

    /// <summary>
    /// Samples in [-1, 1] as a WAV of any encoding: PCM 8/16/24/32-bit or IEEE float 32/64-bit, plain or
    /// <c>WAVE_FORMAT_EXTENSIBLE</c>.
    /// </summary>
    public static string Write(string path, int sampleRate, int channels, int bits, bool isFloat, bool extensible, ReadOnlySpan<float> interleaved)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var bytesPerSample = bits / 8;
        var dataBytes = interleaved.Length * bytesPerSample;
        var fmtSize = extensible ? 40 : 16;
        var tag = (short)(isFloat ? 3 : 1);
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + fmtSize + 8 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(fmtSize);
        writer.Write(extensible ? unchecked((short)0xFFFE) : tag);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write((short)bits);
        if (extensible)
        {
            writer.Write((short)22);
            writer.Write((short)bits);
            writer.Write(channels == 1 ? 0x4 : 0x3);
            writer.Write(tag);
            writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }

        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var x in interleaved)
        {
            switch (isFloat, bits)
            {
                case (true, 32):
                    writer.Write(x);
                    break;
                case (true, 64):
                    writer.Write((double)x);
                    break;
                case (false, 8):
                    writer.Write((byte)Math.Clamp(Math.Round((x * 128) + 128), 0, 255));
                    break;
                case (false, 16):
                    writer.Write((short)Math.Clamp(Math.Round(x * 32768), short.MinValue, short.MaxValue));
                    break;
                case (false, 24):
                    var v = (int)Math.Clamp(Math.Round(x * 8388608), -8388608, 8388607);
                    writer.Write((byte)v);
                    writer.Write((byte)(v >> 8));
                    writer.Write((byte)(v >> 16));
                    break;
                case (false, 32):
                    writer.Write((int)Math.Clamp(Math.Round(x * 2147483648.0), int.MinValue, int.MaxValue));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(bits));
            }
        }

        return path;
    }

    /// <summary>Raw bytes as a file (for damaged headers).</summary>
    public static string Raw(string path, params byte[][] parts)
    {
        using var stream = File.Create(path);
        foreach (var part in parts)
        {
            stream.Write(part);
        }

        return path;
    }

    /// <summary>A RIFF/WAVE header with a compressed format tag (IMA ADPCM) the managed reader does not decode.</summary>
    public static string Adpcm(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + 256);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)0x11);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(8_000);
        writer.Write((short)256);
        writer.Write((short)4);
        writer.Write("data"u8);
        writer.Write(256);
        writer.Write(new byte[256]);
        return path;
    }

    public static (int Rate, int Channels, int Bits, float[] Samples) Read(string path)
    {
        using var reader = WavReader.Open(path);
        var (format, samples) = WavReader.ReadAll(path);
        return (format.SampleRate, format.Channels, reader.Layout.BitsPerSample, samples);
    }
}
