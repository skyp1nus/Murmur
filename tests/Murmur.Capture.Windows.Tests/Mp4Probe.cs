using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>What Media Foundation's source reader sees in a recorded segment.</summary>
internal sealed record Mp4ProbeResult(Guid VideoSubtype, int Width, int Height, int Frames, long LastSampleEnd, long PresentationDuration)
{
    /// <summary>The presentation duration when the file declares one, else the end of the last sample (100 ns).</summary>
    public long Duration => PresentationDuration > 0 ? PresentationDuration : LastSampleEnd;
}

/// <summary>Opens a file with IMFSourceReader (compressed samples, no decoding) and walks its first video stream.</summary>
internal static unsafe class Mp4Probe
{
    public static Mp4ProbeResult Read(string path)
    {
        MediaFoundationRuntime.EnsureStarted();
        nint reader = 0, mediaType = 0;
        try
        {
            fixed (char* pPath = path)
            {
                ComHelpers.Check(ScreenNativeMethods.MFCreateSourceReaderFromURL(pPath, 0, &reader), "MFCreateSourceReaderFromURL");
            }

            ComHelpers.Check(IMFSourceReader.GetCurrentMediaType(reader, ScreenConstants.MfSourceReaderFirstVideoStream, &mediaType), "IMFSourceReader::GetCurrentMediaType");
            var subtypeKey = ScreenConstants.MfMtSubtype;
            Guid subtype;
            ComHelpers.Check(IMFAttributes.GetGUID(mediaType, &subtypeKey, &subtype), "IMFAttributes::GetGUID(MF_MT_SUBTYPE)");
            var sizeKey = ScreenConstants.MfMtFrameSize;
            ulong packedSize = 0;
            IMFAttributes.GetUINT64(mediaType, &sizeKey, &packedSize);

            // PROPVARIANT: vt at 0, value at 8 (VT_UI8 for MF_PD_DURATION).
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();
            var durationKey = ScreenConstants.MfPdDuration;
            long declared = 0;
            if (IMFSourceReader.GetPresentationAttribute(reader, ScreenConstants.MfSourceReaderMediaSource, &durationKey, variant) >= 0
                && *(ushort*)variant == 21)
            {
                declared = *(long*)(variant + 8);
            }

            var frames = 0;
            long end = 0;
            while (true)
            {
                uint actual, flags;
                long timestamp;
                nint sample = 0;
                ComHelpers.Check(
                    IMFSourceReader.ReadSample(reader, ScreenConstants.MfSourceReaderFirstVideoStream, 0, &actual, &flags, &timestamp, &sample),
                    "IMFSourceReader::ReadSample");
                if (sample != 0)
                {
                    frames++;
                    long duration = 0;
                    IMFSample.GetSampleDuration(sample, &duration);
                    end = Math.Max(end, timestamp + duration);
                    ComCall.Release(ref sample);
                }

                if ((flags & (ScreenConstants.MfSourceReaderFlagEndOfStream | ScreenConstants.MfSourceReaderFlagError)) != 0)
                {
                    break;
                }
            }

            return new Mp4ProbeResult(subtype, (int)(packedSize >> 32), (int)(packedSize & 0xFFFF_FFFF), frames, end, declared);
        }
        finally
        {
            ComCall.Release(ref mediaType);
            ComCall.Release(ref reader);
        }
    }
}
