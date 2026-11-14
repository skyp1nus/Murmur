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
/// Decodes audio and video files with Media Foundation (<c>MFCreateSourceReaderFromURL</c>). The first audio stream is
/// read as PCM: the source reader is asked for 32-bit float in the recorder's track format first (16 kHz mono, its own
/// resampler), then for float at the stream's rate, then for 16-bit PCM at the stream's rate. Whatever it delivers is
/// brought to <see cref="AudioFormat.Asr"/> by <see cref="StreamingResampler"/> (a pass-through when Media Foundation
/// already converted) and written as 16-bit PCM.
/// </summary>
/// <remarks>
/// <para><b>Formats.</b> MP3, M4A/AAC, FLAC, WMA, MP4 and MOV come with Windows 10/11. OGG, MKV and WEBM open only
/// when the installed codecs support them (e.g. the Web Media Extensions); otherwise <see cref="ProbeAsync"/> fails with
/// <see cref="NotSupportedException"/> for that file. A file without an audio stream, or one Media Foundation cannot
/// parse, is <see cref="InvalidDataException"/>; a missing file <see cref="FileNotFoundException"/>.</para>
/// <para><b>Timeline.</b> Sample timestamps are honoured: audio that starts after 0 (an MP4 whose sound starts later
/// than its picture) or has gaps (stream ticks) is padded with silence, so the track stays in step with the video and
/// the transcript times match the file. Media Foundation's MPEG-4 source does not apply edit lists, so for <c>.mp4</c>,
/// <c>.m4a</c> and <c>.mov</c> the audio track's edit list is read with <see cref="Mp4EditList"/>: when the first
/// timestamp shows the reader ignored a leading empty edit, the track starts with that much silence and the
/// edit's start trim (<c>media_time</c>) is cut from the decoded audio. A trim without an empty edit is left as Media
/// Foundation delivers it (it cannot be told apart from a trim the reader applied; in practice it is encoder priming of a
/// few tens of milliseconds).</para>
/// <para><b>Threads.</b> Probe and decode run on the thread pool and join the multithreaded apartment explicitly
/// (<c>CoInitializeEx</c>, balanced); raw vtable calls (no RCWs), pooled buffers, nothing allocated per sample. The
/// decode writes <c>&lt;destination&gt;.part</c> and renames it when complete.</para>
/// </remarks>
public sealed class MediaFoundationDecoder : IMediaDecoder
{
    private const int BlockFrames = 8192;

    /// <summary>Blocks of silence written between two cancellation checks (16 x 8192 frames: about 3 s at 44.1 kHz).</summary>
    private const int PadBlocksPerCancelCheck = 16;

    /// <summary>A timestamp this far ahead of the audio written so far is a gap to fill with silence (not codec jitter).</summary>
    private static readonly long GapTolerance = TimeSpan.FromMilliseconds(20).Ticks;

    /// <summary>
    /// The longest leading silence an edit list may ask for when neither Media Foundation nor the <c>mvhd</c> box says how
    /// long the file is (no real recording starts its sound minutes late; a bigger delay is damage). Longer is skipped, not
    /// clipped to this.
    /// </summary>
    internal static readonly TimeSpan UnknownDurationMaxDelay = TimeSpan.FromMinutes(5);

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
            ScreenConstants.MfEInvalidStreamNumber or ScreenConstants.MfENoMoreTypes => new InvalidDataException($"'{name}' has no audio stream Media Foundation can read ({detail})."),
            _ => new InvalidDataException(detail),
        };
    }

    private static unsafe MediaProbe Probe(string path)
    {
        using var apartment = ComHelpers.EnterMta();
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
        using var apartment = ComHelpers.EnterMta();
        var part = destinationWav + ".part";
        var reader = Open(path);
        try
        {
            var declared = DurationOf(reader).Ticks;
            var duration = Math.Max(declared, 1);
            var reported = -1;
            using (var writer = CreateWriter(part))
            using (var pipeline = new Pipeline(CurrentFormat(reader, path), writer, AudioStartOf(path, cancellationToken)))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint actual = 0, flags = 0;
                    long timestamp = 0;
                    nint sample = 0;
                    var hr = IMFSourceReader.ReadSample(reader, ScreenConstants.MfSourceReaderFirstAudioStream, 0, &actual, &flags, &timestamp, &sample);
                    try
                    {
                        if (hr < 0 || (flags & ScreenConstants.MfSourceReaderFlagError) != 0)
                        {
                            throw ErrorFor(hr < 0 ? hr : ScreenConstants.EFail, path, "IMFSourceReader::ReadSample");
                        }

                        if ((flags & ScreenConstants.MfSourceReaderFlagCurrentMediaTypeChanged) != 0)
                        {
                            // A new rate or channel count mid-stream (rare: chained streams): finish the old one, go on with the new.
                            pipeline.Change(CurrentFormat(reader, path));
                        }

                        if (sample != 0 || (flags & ScreenConstants.MfSourceReaderFlagStreamTick) != 0)
                        {
                            timestamp = pipeline.PadTo(timestamp, sample != 0, declared, cancellationToken);
                        }

                        if (sample != 0)
                        {
                            pipeline.Write(sample, path);
                            var percent = (int)Math.Clamp(timestamp * 100 / duration, 0, 99);
                            if (percent != reported)
                            {
                                reported = percent;
                                progress?.Report(percent / 100.0);
                            }
                        }
                    }
                    finally
                    {
                        // Whatever was thrown above, the sample of this read is released.
                        ComCall.Release(ref sample);
                    }

                    if ((flags & ScreenConstants.MfSourceReaderFlagEndOfStream) != 0)
                    {
                        break;
                    }
                }

                pipeline.Finish();
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
            ComCall.Release(ref reader);
        }
    }

    /// <summary>
    /// The edit list the source reader left for the decoder to apply, judged by the timestamp of the reader's first audio
    /// sample: a leading empty edit (longer than codec jitter) is taken as ignored when that timestamp is nearer 0 than
    /// the point the edit puts the first sample at (<c>Delay - Trim</c>; a reader that honours the edit and delivers the
    /// primed frames starts there). Null when there is none, the reader honoured it, or the delay runs more than a second
    /// past the file's duration (damage, not an offset). That duration is the reader's (<paramref name="declaredTicks"/>);
    /// when it is 0 or unknown, the <c>mvhd</c> movie duration of the edit list; when that is unknown too, the delay is
    /// capped at <see cref="UnknownDurationMaxDelay"/>, so a damaged file cannot make the import write hours of silence.
    /// </summary>
    internal static Mp4AudioStart? EditToApply(Mp4AudioStart edit, long firstTimestamp, long declaredTicks)
    {
        var delay = edit.Delay.Ticks;
        if (delay <= GapTolerance)
        {
            return null;
        }

        var bound = declaredTicks > 0 ? declaredTicks
            : edit.MovieDuration.Ticks > 0 ? edit.MovieDuration.Ticks
            : UnknownDurationMaxDelay.Ticks;
        if (delay > bound + TimeSpan.TicksPerSecond)
        {
            return null;
        }

        return firstTimestamp < (delay - edit.Trim.Ticks) / 2 ? edit : null;
    }

    /// <summary>The audio track's edit list of an MP4/M4A/MOV (none for other files, or when it cannot be read).</summary>
    private static Mp4AudioStart AudioStartOf(string path, CancellationToken cancellationToken)
    {
        if (!Mp4EditList.AppliesTo(path))
        {
            return Mp4AudioStart.None;
        }

        var start = Mp4EditList.ReadAudioStart(path, out var problem, cancellationToken);
        if (problem is not null)
        {
            LogWarning($"the edit list of '{Path.GetFileName(path)}' could not be read, the audio is decoded from its first sample: {problem}");
        }

        return start;
    }

    private static void LogWarning(string message)
    {
        try
        {
            Console.Error.WriteLine($"Murmur: import: {message}");
        }
        catch (Exception)
        {
            // A broken console must not break the import.
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

    /// <summary>A source reader on the first audio stream only, delivering PCM (16 kHz mono float when Media Foundation can).</summary>
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

            // The recorder's format straight from Media Foundation; else float at the native rate; else 16-bit PCM (for a
            // decoder that cannot output float when the source reader cannot convert either). Resampled here as needed.
            if (SetOutputType(reader, ScreenConstants.MfAudioFormatFloat, AudioFormat.Asr) < 0
                && SetOutputType(reader, ScreenConstants.MfAudioFormatFloat, null) < 0)
            {
                hr = SetOutputType(reader, ScreenConstants.MfAudioFormatPcm, null);
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

    /// <summary>
    /// PCM output of <paramref name="subtype"/> (float: 32 bits, PCM: 16 bits): of <paramref name="format"/>, or (null) at
    /// whatever rate and channel count the stream has.
    /// </summary>
    private static unsafe int SetOutputType(nint reader, Guid subtype, AudioFormat? format)
    {
        nint type = 0;
        try
        {
            var hr = ScreenNativeMethods.MFCreateMediaType(&type);
            if (hr < 0)
            {
                return hr;
            }

            var bits = subtype == ScreenConstants.MfAudioFormatFloat ? 32u : 16u;
            var key = ScreenConstants.MfMtMajorType;
            var value = ScreenConstants.MfMediaTypeAudio;
            IMFAttributes.SetGUID(type, &key, &value);
            key = ScreenConstants.MfMtSubtype;
            value = subtype;
            IMFAttributes.SetGUID(type, &key, &value);
            key = ScreenConstants.MfMtAudioBitsPerSample;
            IMFAttributes.SetUINT32(type, &key, bits);
            if (format is { } f)
            {
                var blockAlign = (uint)f.Channels * (bits / 8);
                key = ScreenConstants.MfMtAudioSamplesPerSecond;
                IMFAttributes.SetUINT32(type, &key, (uint)f.SampleRate);
                key = ScreenConstants.MfMtAudioNumChannels;
                IMFAttributes.SetUINT32(type, &key, (uint)f.Channels);
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

    /// <summary>Rate, channel count and encoding of the PCM the reader delivers now.</summary>
    /// <exception cref="NotSupportedException">Anything but 32-bit float or 16-bit PCM.</exception>
    private static unsafe PcmFormat CurrentFormat(nint reader, string path)
    {
        nint type = 0;
        try
        {
            var hr = IMFSourceReader.GetCurrentMediaType(reader, ScreenConstants.MfSourceReaderFirstAudioStream, &type);
            if (hr < 0)
            {
                throw ErrorFor(hr, path, "IMFSourceReader::GetCurrentMediaType");
            }

            uint rate = 0, channels = 0, bits = 0;
            Guid subtype = default;
            var key = ScreenConstants.MfMtAudioSamplesPerSecond;
            IMFAttributes.GetUINT32(type, &key, &rate);
            key = ScreenConstants.MfMtAudioNumChannels;
            IMFAttributes.GetUINT32(type, &key, &channels);
            key = ScreenConstants.MfMtAudioBitsPerSample;
            IMFAttributes.GetUINT32(type, &key, &bits);
            key = ScreenConstants.MfMtSubtype;
            IMFAttributes.GetGUID(type, &key, &subtype);
            if (rate == 0 || channels == 0)
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' has an audio stream without a sample rate or channel count.");
            }

            bool pcm16;
            if (subtype == ScreenConstants.MfAudioFormatFloat && bits == 32)
            {
                pcm16 = false;
            }
            else if (subtype == ScreenConstants.MfAudioFormatPcm && bits == 16)
            {
                pcm16 = true;
            }
            else
            {
                throw new NotSupportedException($"'{Path.GetFileName(path)}' decodes to {subtype} with {bits} bits, not float or 16-bit PCM.");
            }

            return new PcmFormat(new AudioFormat((int)rate, (int)channels), pcm16);
        }
        finally
        {
            ComCall.Release(ref type);
        }
    }

    /// <summary>MF_PD_DURATION of the media source (100 ns units, VT_UI8); zero when the file does not declare it.</summary>
    private static unsafe TimeSpan DurationOf(nint reader)
    {
        var value = default(PropVariant);
        var key = ScreenConstants.MfPdDuration;
        if (IMFSourceReader.GetPresentationAttribute(reader, ScreenConstants.MfSourceReaderMediaSource, &key, &value) < 0)
        {
            return TimeSpan.Zero;
        }

        try
        {
            return value.VarType == ScreenConstants.VtUI8 ? TimeSpan.FromTicks((long)Math.Min((ulong)value.Value, long.MaxValue)) : TimeSpan.Zero;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
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

    /// <param name="Pcm16">16-bit PCM samples; otherwise 32-bit float.</param>
    internal readonly record struct PcmFormat(AudioFormat Audio, bool Pcm16);

    /// <summary>
    /// The PCM -&gt; track conversion of one decode: the resampler of the current format, its pooled buffers, the
    /// position on the file's timeline up to which audio was written (to pad gaps with silence), and the edit list the
    /// source reader did not apply.
    /// </summary>
    internal sealed class Pipeline : IDisposable
    {
        private readonly WavPcm16Writer _writer;
        private PcmFormat _format;
        private StreamingResampler _resampler;
        private float[] _output = [];
        private float[] _scratch = [];
        private readonly Mp4AudioStart _edit;
        private bool _started;
        private long _shiftTicks;
        private long _skipFrames;
        private long _baseTicks;
        private long _fedFrames;

        public Pipeline(PcmFormat format, WavPcm16Writer writer, Mp4AudioStart edit)
        {
            _writer = writer;
            _format = format;
            _edit = edit;
            _resampler = new StreamingResampler(format.Audio, AudioFormat.Asr.SampleRate);
            EnsureBuffers();
        }

        /// <summary>Where on the file's timeline (100 ns) the audio written so far ends.</summary>
        private long CoveredTicks => _baseTicks + (_fedFrames * TimeSpan.TicksPerSecond / _format.Audio.SampleRate);

        public void Change(PcmFormat format)
        {
            if (format == _format)
            {
                return;
            }

            var covered = CoveredTicks;
            _writer.Append(_output.AsSpan(0, _resampler.Flush(_output)));
            _skipFrames = _skipFrames * format.Audio.SampleRate / _format.Audio.SampleRate;
            _format = format;
            _baseTicks = covered;
            _fedFrames = 0;
            _resampler = new StreamingResampler(format.Audio, AudioFormat.Asr.SampleRate);
            EnsureBuffers();
        }

        /// <summary>
        /// Fills a gap before <paramref name="timestamp"/> (the reader's, of a sample or of a stream tick) with silence (a
        /// timestamp far past the declared end is ignored); returns it on the file's timeline. The first call with a real
        /// <paramref name="sample"/> decides whether the reader applied the edit list (<see cref="EditToApply"/>): if not,
        /// the track starts with the empty edit's silence, every timestamp moves by the edit and its start trim is
        /// dropped from the audio. Stream ticks before it only fill gaps.
        /// </summary>
        /// <exception cref="OperationCanceledException">Cancelled while writing silence.</exception>
        public long PadTo(long timestamp, bool sample, long declaredTicks, CancellationToken cancellationToken)
        {
            if (!_started && sample)
            {
                _started = true;
                if (EditToApply(_edit, timestamp, declaredTicks) is { } edit)
                {
                    _shiftTicks = edit.Delay.Ticks - edit.Trim.Ticks;
                    _skipFrames = edit.Trim.Ticks * _format.Audio.SampleRate / TimeSpan.TicksPerSecond;
                    Pad(edit.Delay.Ticks - CoveredTicks, cancellationToken);
                }
            }

            timestamp += _shiftTicks;
            var gap = timestamp - CoveredTicks;
            if (gap > GapTolerance && (declaredTicks <= 0 || timestamp <= declaredTicks + TimeSpan.TicksPerSecond))
            {
                Pad(gap, cancellationToken);
            }

            return timestamp;
        }

        public unsafe void Write(nint sample, string path)
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
                    var channels = _format.Audio.Channels;
                    if (_format.Pcm16)
                    {
                        var frames = (int)(length / sizeof(short) / (uint)channels);
                        var samples = new ReadOnlySpan<short>(data, frames * channels);
                        var skip = Skip(frames);
                        for (var offset = skip; offset < frames; offset += BlockFrames)
                        {
                            var count = Math.Min(BlockFrames, frames - offset);
                            var block = samples.Slice(offset * channels, count * channels);
                            var converted = _scratch.AsSpan(0, block.Length);
                            for (var i = 0; i < block.Length; i++)
                            {
                                converted[i] = block[i] / 32768f;
                            }

                            Feed(converted);
                        }
                    }
                    else
                    {
                        var frames = (int)(length / sizeof(float) / (uint)channels);
                        Write(new ReadOnlySpan<float>(data, frames * channels));
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

        /// <summary>Interleaved float audio of the current format (what is left of the start trim is dropped first).</summary>
        public void Write(ReadOnlySpan<float> interleaved)
        {
            var channels = _format.Audio.Channels;
            var frames = interleaved.Length / channels;
            for (var offset = Skip(frames); offset < frames; offset += BlockFrames)
            {
                var count = Math.Min(BlockFrames, frames - offset);
                Feed(interleaved.Slice(offset * channels, count * channels));
            }
        }

        public void Finish() => _writer.Append(_output.AsSpan(0, _resampler.Flush(_output)));

        public void Dispose()
        {
            Return(ref _output);
            Return(ref _scratch);
        }

        /// <summary>How many of the <paramref name="frames"/> of a sample are still in the edit's start trim (dropped).</summary>
        private int Skip(int frames)
        {
            var skip = (int)Math.Min(_skipFrames, frames);
            _skipFrames -= skip;
            return skip;
        }

        /// <summary><paramref name="ticks"/> of silence (nothing when not positive); cancellable every few blocks.</summary>
        private void Pad(long ticks, CancellationToken cancellationToken)
        {
            var frames = ticks * _format.Audio.SampleRate / TimeSpan.TicksPerSecond;
            var channels = _format.Audio.Channels;
            for (var block = 0; frames > 0; block++)
            {
                if (block % PadBlocksPerCancelCheck == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var count = (int)Math.Min(frames, BlockFrames);
                var silence = _scratch.AsSpan(0, count * channels);
                silence.Clear();
                Feed(silence);
                frames -= count;
            }
        }

        /// <summary>Gives a rented array back once: the field is emptied first, so a later failing Rent never returns it twice.</summary>
        private static void Return(ref float[] array)
        {
            var rented = array;
            array = [];
            if (rented.Length > 0)
            {
                ArrayPool<float>.Shared.Return(rented);
            }
        }

        private void Feed(ReadOnlySpan<float> interleaved)
        {
            var produced = _resampler.Process(interleaved, _output);
            _writer.Append(_output.AsSpan(0, produced));
            _fedFrames += interleaved.Length / _format.Audio.Channels;
        }

        private void EnsureBuffers()
        {
            var output = Math.Max(_resampler.MaxOutputSamples(BlockFrames), _resampler.MaxFlushSamples);
            if (_output.Length < output)
            {
                Return(ref _output);
                _output = ArrayPool<float>.Shared.Rent(output);
            }

            var scratch = BlockFrames * _format.Audio.Channels;
            if (_scratch.Length < scratch)
            {
                Return(ref _scratch);
                _scratch = ArrayPool<float>.Shared.Rent(scratch);
            }
        }
    }
}
