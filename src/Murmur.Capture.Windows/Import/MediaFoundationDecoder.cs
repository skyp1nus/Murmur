using System.Buffers;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Import;

namespace Murmur.Capture.Windows.Import;

/// <summary>
/// Decodes audio and video files with Media Foundation (<c>MFCreateSourceReaderFromURL</c>): the first audio stream
/// is read as 32-bit float, and the source reader is first asked for the recorder's track format directly (16 kHz
/// mono, its resampler); whatever rate and channel count it delivers is then brought to <see cref="AudioFormat.Asr"/>
/// by <see cref="StreamingResampler"/> (a pass-through when Media Foundation already converted) and written as 16-bit PCM.
/// </summary>
/// <remarks>
/// <para><b>Formats.</b> MP3, M4A/AAC, FLAC, WMA, MP4 and MOV come with Windows 10/11. OGG, MKV and WEBM open only
/// when the installed codecs support them (e.g. the Web Media Extensions); otherwise <see cref="ProbeAsync"/> fails with
/// <see cref="NotSupportedException"/> for that file. A file without an audio stream, or one Media Foundation cannot
/// parse, is <see cref="InvalidDataException"/>; a missing file <see cref="FileNotFoundException"/>.</para>
/// <para><b>Threads.</b> Probe and decode run on the thread pool (multithreaded apartment); raw vtable calls (no RCWs),
/// pooled buffers, nothing allocated per sample. The decode writes <c>&lt;destination&gt;.part</c> and renames it when
/// complete. UNVERIFIED-ON-WINDOWS until the owner's check (#66).</para>
/// </remarks>
public sealed class MediaFoundationDecoder : IMediaDecoder
{
    private const int BlockFrames = 8192;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".mp4", ".mov", ".mkv", ".webm",
    };

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public ValueTask<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new ValueTask<MediaProbe>(Task.Run(() => Probe(path), cancellationToken));
    }

    public ValueTask DecodeAudioAsync(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(destinationWav);
        return new ValueTask(Task.Run(() => Decode(path, destinationWav, progress, cancellationToken), cancellationToken));
    }

    /// <summary>The exception for a failed open or read: format, damage, missing file or access.</summary>
    internal static Exception ErrorFor(int hr, string path, string what)
    {
        var name = Path.GetFileName(path);
        var detail = $"{what} failed for '{name}': HRESULT 0x{hr:X8}";
        return hr switch
        {
            ScreenConstants.EFileNotFound or ScreenConstants.EPathNotFound => new FileNotFoundException(detail, path),
            ScreenConstants.EAccessDenied => new UnauthorizedAccessException(detail),
            ScreenConstants.MfEUnsupportedByteStreamType or ScreenConstants.MfEUnsupportedScheme
                or ScreenConstants.MfETopoCodecNotFound or ScreenConstants.MfEInvalidMediaType => new NotSupportedException(detail),
            ScreenConstants.MfEInvalidStreamNumber or ScreenConstants.MfENoMoreTypes => new InvalidDataException($"'{name}' has no audio stream."),
            _ => new InvalidDataException(detail),
        };
    }

    private static unsafe MediaProbe Probe(string path)
    {
        var reader = Open(path);
        nint video = 0;
        try
        {
            var kind = IMFSourceReader.GetNativeMediaType(reader, ScreenConstants.MfSourceReaderFirstVideoStream, 0, &video) >= 0
                ? MediaKind.Video
                : MediaKind.Audio;
            return new MediaProbe(path, kind, new FileInfo(path).Length, DurationOf(reader));
        }
        finally
        {
            ComCall.Release(ref video);
            ComCall.Release(ref reader);
        }
    }

    private static unsafe void Decode(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var part = destinationWav + ".part";
        var reader = Open(path);
        float[]? output = null;
        try
        {
            var duration = Math.Max(DurationOf(reader).Ticks, 1);
            var format = CurrentFormat(reader, path);
            var resampler = new StreamingResampler(format, AudioFormat.Asr.SampleRate);
            output = ArrayPool<float>.Shared.Rent(Math.Max(resampler.MaxOutputSamples(BlockFrames), resampler.MaxFlushSamples));
            var reported = -1;

            using (var writer = CreateWriter(part))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint actual, flags;
                    long timestamp;
                    nint sample = 0;
                    var hr = IMFSourceReader.ReadSample(reader, ScreenConstants.MfSourceReaderFirstAudioStream, 0, &actual, &flags, &timestamp, &sample);
                    if (hr < 0 || (flags & ScreenConstants.MfSourceReaderFlagError) != 0)
                    {
                        ComCall.Release(ref sample);
                        throw ErrorFor(hr < 0 ? hr : ScreenConstants.EFail, path, "IMFSourceReader::ReadSample");
                    }

                    if ((flags & ScreenConstants.MfSourceReaderFlagCurrentMediaTypeChanged) != 0)
                    {
                        // A new rate or channel count mid-stream (rare: chained streams): finish the old one, go on with the new.
                        var changed = CurrentFormat(reader, path);
                        if (changed != resampler.InputFormat)
                        {
                            writer.Append(output.AsSpan(0, resampler.Flush(output)));
                            resampler = new StreamingResampler(changed, AudioFormat.Asr.SampleRate);
                            var needed = Math.Max(resampler.MaxOutputSamples(BlockFrames), resampler.MaxFlushSamples);
                            if (output.Length < needed)
                            {
                                ArrayPool<float>.Shared.Return(output);
                                output = ArrayPool<float>.Shared.Rent(needed);
                            }
                        }
                    }

                    if (sample != 0)
                    {
                        try
                        {
                            WriteSample(sample, resampler, output, writer, path);
                        }
                        finally
                        {
                            ComCall.Release(ref sample);
                        }

                        var percent = (int)Math.Clamp(timestamp * 100 / duration, 0, 99);
                        if (percent != reported)
                        {
                            reported = percent;
                            progress?.Report(percent / 100.0);
                        }
                    }

                    if ((flags & ScreenConstants.MfSourceReaderFlagEndOfStream) != 0)
                    {
                        break;
                    }
                }

                writer.Append(output.AsSpan(0, resampler.Flush(output)));
                if (writer.SamplesWritten == 0)
                {
                    throw new InvalidDataException($"'{Path.GetFileName(path)}' holds no audio.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(part, destinationWav, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
        finally
        {
            if (output is not null)
            {
                ArrayPool<float>.Shared.Return(output);
            }

            ComCall.Release(ref reader);
        }
    }

    /// <summary>A 16 kHz mono PCM16 writer over a new <paramref name="path"/>; the stream is disposed when the writer cannot be made.</summary>
    private static WavPcm16Writer CreateWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        try
        {
            return new WavPcm16Writer(stream, AudioFormat.Asr.SampleRate, AudioFormat.Asr.Channels);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Converts one decoded float sample in blocks of <see cref="BlockFrames"/> frames.</summary>
    private static unsafe void WriteSample(nint sample, StreamingResampler resampler, float[] output, WavPcm16Writer writer, string path)
    {
        nint buffer = 0;
        try
        {
            var hr = IMFSample.ConvertToContiguousBuffer(sample, &buffer);
            if (hr < 0)
            {
                throw ErrorFor(hr, path, "IMFSample::ConvertToContiguousBuffer");
            }

            byte* data;
            uint max, length;
            hr = IMFMediaBuffer.Lock(buffer, &data, &max, &length);
            if (hr < 0)
            {
                throw ErrorFor(hr, path, "IMFMediaBuffer::Lock");
            }

            try
            {
                var channels = resampler.InputFormat.Channels;
                var frames = (int)(length / sizeof(float) / (uint)channels);
                var samples = new ReadOnlySpan<float>(data, frames * channels);
                for (var offset = 0; offset < frames; offset += BlockFrames)
                {
                    var count = Math.Min(BlockFrames, frames - offset);
                    var produced = resampler.Process(samples.Slice(offset * channels, count * channels), output);
                    writer.Append(output.AsSpan(0, produced));
                }
            }
            finally
            {
                IMFMediaBuffer.Unlock(buffer);
            }
        }
        finally
        {
            ComCall.Release(ref buffer);
        }
    }

    /// <summary>A source reader on the first audio stream only, delivering float PCM (16 kHz mono when Media Foundation can).</summary>
    private static unsafe nint Open(string path)
    {
        try
        {
            MediaFoundationRuntime.EnsureStarted();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows N / Server without the Media Feature Pack.
            throw new NotSupportedException("Media Foundation is not available on this PC.", ex);
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"'{Path.GetFileName(path)}' does not exist.", path);
        }

        nint reader = 0;
        try
        {
            int hr;
            fixed (char* url = full)
            {
                hr = ScreenNativeMethods.MFCreateSourceReaderFromURL(url, 0, &reader);
            }

            if (hr < 0)
            {
                throw ErrorFor(hr, path, "MFCreateSourceReaderFromURL");
            }

            IMFSourceReader.SetStreamSelection(reader, ScreenConstants.MfSourceReaderAllStreams, 0);
            hr = IMFSourceReader.SetStreamSelection(reader, ScreenConstants.MfSourceReaderFirstAudioStream, 1);
            if (hr < 0)
            {
                throw ErrorFor(hr, path, "IMFSourceReader::SetStreamSelection");
            }

            // The recorder's format straight from Media Foundation; if it refuses, float at the native rate (resampled here).
            if (SetOutputType(reader, AudioFormat.Asr) < 0)
            {
                hr = SetOutputType(reader, null);
                if (hr < 0)
                {
                    throw ErrorFor(hr, path, "IMFSourceReader::SetCurrentMediaType");
                }
            }

            return reader;
        }
        catch
        {
            ComCall.Release(ref reader);
            throw;
        }
    }

    /// <summary>Float PCM output: of <paramref name="format"/>, or (null) at whatever rate and channel count the stream has.</summary>
    private static unsafe int SetOutputType(nint reader, AudioFormat? format)
    {
        nint type = 0;
        try
        {
            var hr = ScreenNativeMethods.MFCreateMediaType(&type);
            if (hr < 0)
            {
                return hr;
            }

            var key = ScreenConstants.MfMtMajorType;
            var value = ScreenConstants.MfMediaTypeAudio;
            IMFAttributes.SetGUID(type, &key, &value);
            key = ScreenConstants.MfMtSubtype;
            value = ScreenConstants.MfAudioFormatFloat;
            IMFAttributes.SetGUID(type, &key, &value);
            if (format is { } f)
            {
                var blockAlign = (uint)(f.Channels * sizeof(float));
                key = ScreenConstants.MfMtAudioSamplesPerSecond;
                IMFAttributes.SetUINT32(type, &key, (uint)f.SampleRate);
                key = ScreenConstants.MfMtAudioNumChannels;
                IMFAttributes.SetUINT32(type, &key, (uint)f.Channels);
                key = ScreenConstants.MfMtAudioBitsPerSample;
                IMFAttributes.SetUINT32(type, &key, 32);
                key = ScreenConstants.MfMtAudioBlockAlignment;
                IMFAttributes.SetUINT32(type, &key, blockAlign);
                key = ScreenConstants.MfMtAudioAvgBytesPerSecond;
                IMFAttributes.SetUINT32(type, &key, blockAlign * (uint)f.SampleRate);
            }

            return IMFSourceReader.SetCurrentMediaType(reader, ScreenConstants.MfSourceReaderFirstAudioStream, null, type);
        }
        finally
        {
            ComCall.Release(ref type);
        }
    }

    /// <summary>Rate and channel count of the float PCM the reader delivers now.</summary>
    private static unsafe AudioFormat CurrentFormat(nint reader, string path)
    {
        nint type = 0;
        try
        {
            var hr = IMFSourceReader.GetCurrentMediaType(reader, ScreenConstants.MfSourceReaderFirstAudioStream, &type);
            if (hr < 0)
            {
                throw ErrorFor(hr, path, "IMFSourceReader::GetCurrentMediaType");
            }

            uint rate = 0, channels = 0;
            var key = ScreenConstants.MfMtAudioSamplesPerSecond;
            IMFAttributes.GetUINT32(type, &key, &rate);
            key = ScreenConstants.MfMtAudioNumChannels;
            IMFAttributes.GetUINT32(type, &key, &channels);
            if (rate == 0 || channels == 0)
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' has an audio stream without a sample rate or channel count.");
            }

            return new AudioFormat((int)rate, (int)channels);
        }
        finally
        {
            ComCall.Release(ref type);
        }
    }

    /// <summary>MF_PD_DURATION of the media source (100 ns units, VT_UI8); zero when the file does not declare it.</summary>
    private static unsafe TimeSpan DurationOf(nint reader)
    {
        var variant = default(Variant);
        var key = ScreenConstants.MfPdDuration;
        if (IMFSourceReader.GetPresentationAttribute(reader, ScreenConstants.MfSourceReaderMediaSource, &key, &variant) < 0)
        {
            return TimeSpan.Zero;
        }

        try
        {
            return variant.Vt == ScreenConstants.VtUI8 ? TimeSpan.FromTicks((long)Math.Min(variant.UI8, long.MaxValue)) : TimeSpan.Zero;
        }
        finally
        {
            ScreenNativeMethods.VariantClear(&variant);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover .part never looks like a finished track.
        }
    }
}
