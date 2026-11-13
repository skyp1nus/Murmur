using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>An audio encoding the in-box Media Foundation encoders can write.</summary>
/// <param name="AvgBytesPerSecond">MF_MT_AUDIO_AVG_BYTES_PER_SECOND of the encoded stream, or 0 to leave it to the encoder.</param>
internal sealed record TestAudioCodec(string Name, Guid Subtype, int SampleRate, int Channels, uint AvgBytesPerSecond);

/// <summary>
/// Compressed media files for the import tests, written by the in-box Media Foundation sink writer (no desktop, no GPU,
/// software encoders only): a 440 Hz tone of amplitude <see cref="ToneAmplitude"/> on every channel, optionally with an
/// H.264 picture (320x240, 30 fps, one solid colour). Windows only.
/// </summary>
internal static unsafe class TestMedia
{
    public const float ToneAmplitude = 0.5f;
    public const int ToneHz = 440;

    /// <summary>AAC-LC 44.1 kHz stereo, 128 kbit/s (MFAudioFormat_AAC; in every Windows 10/11 edition but N).</summary>
    public static readonly TestAudioCodec Aac = new("AAC", new Guid("00001610-0000-0010-8000-00AA00389B71"), 44_100, 2, 16_000);

    /// <summary>MP3 44.1 kHz stereo, 128 kbit/s (MFAudioFormat_MP3; the MP3 encoder MFT of Windows 8 and later).</summary>
    public static readonly TestAudioCodec Mp3 = new("MP3", new Guid("00000055-0000-0010-8000-00AA00389B71"), 44_100, 2, 16_000);

    /// <summary>FLAC 48 kHz stereo (MFAudioFormat_FLAC; the FLAC encoder MFT of Windows 10).</summary>
    public static readonly TestAudioCodec Flac = new("FLAC", new Guid("0000F1AC-0000-0010-8000-00AA00389B71"), 48_000, 2, 0);

    private const int Width = 320;
    private const int Height = 240;
    private const int FrameRate = 30;
    private static readonly long AudioChunk = TimeSpan.FromMilliseconds(100).Ticks;

    /// <summary>
    /// Writes <paramref name="path"/> (the container follows the extension; <c>.m4a</c>/<c>.mp4</c> are MPEG-4). The audio
    /// starts at <paramref name="audioStart"/> and ends with the file.
    /// </summary>
    /// <exception cref="COMException">The sink writer or an encoder refused the file (no such encoder on this PC).</exception>
    public static void Write(string path, TestAudioCodec? audio, bool video, TimeSpan duration, TimeSpan audioStart = default)
    {
        MediaFoundationRuntime.EnsureStarted();
        nint attributes = 0, writer = 0;
        try
        {
            ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".mp4" or ".m4a")
            {
                SetGuid(attributes, ScreenConstants.MfTranscodeContainerType, ScreenConstants.MfTranscodeContainerTypeFmpeg4);
            }

            SetUInt32(attributes, ScreenConstants.MfReadwriteEnableHardwareTransforms, 0);
            fixed (char* pPath = path)
            {
                ComHelpers.Check(ScreenNativeMethods.MFCreateSinkWriterFromURL(pPath, 0, attributes, &writer), "MFCreateSinkWriterFromURL");
            }

            var videoStream = video ? AddVideoStream(writer) : uint.MaxValue;
            var audioStream = audio is not null ? AddAudioStream(writer, audio) : uint.MaxValue;
            ComHelpers.Check(IMFSinkWriter.BeginWriting(writer), "IMFSinkWriter::BeginWriting");

            // Interleaved by time, as a muxer wants it.
            var end = duration.Ticks;
            var frameTicks = TimeSpan.TicksPerSecond / FrameRate;
            long nextFrame = video ? 0 : long.MaxValue;
            var nextAudio = audio is not null ? audioStart.Ticks : long.MaxValue;
            long audioFrames = 0;
            while (nextFrame < end || nextAudio < end)
            {
                if (nextFrame <= nextAudio)
                {
                    WriteVideoFrame(writer, videoStream, nextFrame, frameTicks);
                    nextFrame += frameTicks;
                }
                else
                {
                    var chunk = Math.Min(AudioChunk, end - nextAudio);
                    var frames = (int)(chunk * audio!.SampleRate / TimeSpan.TicksPerSecond);
                    WriteAudioChunk(writer, audioStream, audio, nextAudio, chunk, audioFrames, frames);
                    audioFrames += frames;
                    nextAudio += chunk;
                }
            }

            ComHelpers.Check(IMFSinkWriter.Finalize(writer), "IMFSinkWriter::Finalize");
        }
        finally
        {
            ComCall.Release(ref writer);
            ComCall.Release(ref attributes);
        }
    }

    /// <summary>
    /// <see cref="Write"/> for the formats every desktop Windows 10/11 can write (AAC, H.264): a refusal fails the test on a
    /// PC, and skips it only on a GitHub runner (Windows Server images may lack the Media Foundation encoders).
    /// </summary>
    public static void WriteRequired(string path, TestAudioCodec? audio, bool video, TimeSpan duration, TimeSpan audioStart = default)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            WriteOrSkip(path, audio, video, duration, audioStart);
            return;
        }

        Write(path, audio, video, duration, audioStart);
    }

    /// <summary>Like <see cref="Write"/>, but skips the test when this PC has no encoder or sink for that file.</summary>
    public static void WriteOrSkip(string path, TestAudioCodec? audio, bool video, TimeSpan duration, TimeSpan audioStart = default)
    {
        try
        {
            Write(path, audio, video, duration, audioStart);
        }
        catch (COMException ex)
        {
            Assert.Skip($"Media Foundation cannot write {Path.GetExtension(path)} ({audio?.Name ?? "no audio"}{(video ? " + H.264" : string.Empty)}) on this PC: {ex.Message}");
        }
    }

    private static uint AddVideoStream(nint writer)
    {
        nint output = 0, input = 0;
        try
        {
            var size = ((ulong)Width << 32) | Height;
            var rate = ((ulong)FrameRate << 32) | 1;
            var aspect = (1UL << 32) | 1;
            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&output), "MFCreateMediaType");
            SetGuid(output, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeVideo);
            SetGuid(output, ScreenConstants.MfMtSubtype, ScreenConstants.MfVideoFormatH264);
            SetUInt32(output, ScreenConstants.MfMtAvgBitrate, 1_000_000);
            SetUInt32(output, ScreenConstants.MfMtInterlaceMode, ScreenConstants.MfVideoInterlaceProgressive);
            SetUInt64(output, ScreenConstants.MfMtFrameSize, size);
            SetUInt64(output, ScreenConstants.MfMtFrameRate, rate);
            SetUInt64(output, ScreenConstants.MfMtPixelAspectRatio, aspect);
            uint stream;
            ComHelpers.Check(IMFSinkWriter.AddStream(writer, output, &stream), "IMFSinkWriter::AddStream(H.264)");

            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&input), "MFCreateMediaType");
            SetGuid(input, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeVideo);
            SetGuid(input, ScreenConstants.MfMtSubtype, ScreenConstants.MfVideoFormatRgb32);
            SetUInt32(input, ScreenConstants.MfMtInterlaceMode, ScreenConstants.MfVideoInterlaceProgressive);
            SetUInt32(input, ScreenConstants.MfMtDefaultStride, Width * 4);
            SetUInt64(input, ScreenConstants.MfMtFrameSize, size);
            SetUInt64(input, ScreenConstants.MfMtFrameRate, rate);
            SetUInt64(input, ScreenConstants.MfMtPixelAspectRatio, aspect);
            ComHelpers.Check(IMFSinkWriter.SetInputMediaType(writer, stream, input, 0), "IMFSinkWriter::SetInputMediaType(RGB32)");
            return stream;
        }
        finally
        {
            ComCall.Release(ref input);
            ComCall.Release(ref output);
        }
    }

    private static uint AddAudioStream(nint writer, TestAudioCodec codec)
    {
        nint output = 0, input = 0;
        try
        {
            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&output), "MFCreateMediaType");
            SetGuid(output, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeAudio);
            SetGuid(output, ScreenConstants.MfMtSubtype, codec.Subtype);
            SetUInt32(output, ScreenConstants.MfMtAudioBitsPerSample, 16);
            SetUInt32(output, ScreenConstants.MfMtAudioSamplesPerSecond, (uint)codec.SampleRate);
            SetUInt32(output, ScreenConstants.MfMtAudioNumChannels, (uint)codec.Channels);
            if (codec.AvgBytesPerSecond > 0)
            {
                SetUInt32(output, ScreenConstants.MfMtAudioAvgBytesPerSecond, codec.AvgBytesPerSecond);
            }

            uint stream;
            ComHelpers.Check(IMFSinkWriter.AddStream(writer, output, &stream), $"IMFSinkWriter::AddStream({codec.Name})");

            var blockAlign = (uint)(codec.Channels * 2);
            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&input), "MFCreateMediaType");
            SetGuid(input, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeAudio);
            SetGuid(input, ScreenConstants.MfMtSubtype, ScreenConstants.MfAudioFormatPcm);
            SetUInt32(input, ScreenConstants.MfMtAudioBitsPerSample, 16);
            SetUInt32(input, ScreenConstants.MfMtAudioSamplesPerSecond, (uint)codec.SampleRate);
            SetUInt32(input, ScreenConstants.MfMtAudioNumChannels, (uint)codec.Channels);
            SetUInt32(input, ScreenConstants.MfMtAudioBlockAlignment, blockAlign);
            SetUInt32(input, ScreenConstants.MfMtAudioAvgBytesPerSecond, blockAlign * (uint)codec.SampleRate);
            ComHelpers.Check(IMFSinkWriter.SetInputMediaType(writer, stream, input, 0), $"IMFSinkWriter::SetInputMediaType(PCM -> {codec.Name})");
            return stream;
        }
        finally
        {
            ComCall.Release(ref input);
            ComCall.Release(ref output);
        }
    }

    private static void WriteVideoFrame(nint writer, uint stream, long time, long duration)
    {
        const uint Length = Width * Height * 4;
        WriteSample(writer, stream, time, duration, Length, data =>
        {
            new Span<uint>(data, (int)(Length / 4)).Fill(0xFF20_60A0u); // one colour: B, G, R, X bytes
        });
    }

    private static void WriteAudioChunk(nint writer, uint stream, TestAudioCodec codec, long time, long duration, long firstFrame, int frames)
    {
        var length = (uint)(frames * codec.Channels * 2);
        WriteSample(writer, stream, time, duration, length, data =>
        {
            var pcm = new Span<short>(data, frames * codec.Channels);
            for (var i = 0; i < frames; i++)
            {
                var value = (short)(ToneAmplitude * 32767 * Math.Sin(2 * Math.PI * ToneHz * (firstFrame + i) / codec.SampleRate));
                for (var c = 0; c < codec.Channels; c++)
                {
                    pcm[(i * codec.Channels) + c] = value;
                }
            }
        });
    }

    private delegate void Fill(byte* data);

    private static void WriteSample(nint writer, uint stream, long time, long duration, uint length, Fill fill)
    {
        nint sample = 0, buffer = 0;
        try
        {
            ComHelpers.Check(ScreenNativeMethods.MFCreateSample(&sample), "MFCreateSample");
            ComHelpers.Check(ScreenNativeMethods.MFCreateMemoryBuffer(length, &buffer), "MFCreateMemoryBuffer");
            byte* data;
            uint max, current;
            ComHelpers.Check(IMFMediaBuffer.Lock(buffer, &data, &max, &current), "IMFMediaBuffer::Lock");
            try
            {
                fill(data);
            }
            finally
            {
                IMFMediaBuffer.Unlock(buffer);
            }

            ComHelpers.Check(IMFMediaBuffer.SetCurrentLength(buffer, length), "IMFMediaBuffer::SetCurrentLength");
            ComHelpers.Check(IMFSample.AddBuffer(sample, buffer), "IMFSample::AddBuffer");
            ComHelpers.Check(IMFSample.SetSampleTime(sample, time), "IMFSample::SetSampleTime");
            ComHelpers.Check(IMFSample.SetSampleDuration(sample, duration), "IMFSample::SetSampleDuration");
            ComHelpers.Check(IMFSinkWriter.WriteSample(writer, stream, sample), "IMFSinkWriter::WriteSample");
        }
        finally
        {
            ComCall.Release(ref buffer);
            ComCall.Release(ref sample);
        }
    }

    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        ComHelpers.Check(IMFAttributes.SetGUID(attributes, &key, &value), "IMFAttributes::SetGUID");

    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        ComHelpers.Check(IMFAttributes.SetUINT32(attributes, &key, value), "IMFAttributes::SetUINT32");

    private static void SetUInt64(nint attributes, Guid key, ulong value) =>
        ComHelpers.Check(IMFAttributes.SetUINT64(attributes, &key, value), "IMFAttributes::SetUINT64");
}
