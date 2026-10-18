using Murmur.Capture.Windows.Audio;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

public sealed class WaveFormatParserTests
{
    private static readonly Guid FloatSub = WaveFormatParser.KsDataFormatSubtypeIeeeFloat;
    private static readonly Guid PcmSub = WaveFormatParser.KsDataFormatSubtypePcm;

    [Fact]
    public void SubFormatGuids_MatchKsMedia()
    {
        Assert.Equal(new Guid("00000001-0000-0010-8000-00aa00389b71"), PcmSub);
        Assert.Equal(new Guid("00000003-0000-0010-8000-00aa00389b71"), FloatSub);

        // DEFINE_WAVEFORMATEX_GUID(x): Data1 = format tag, so the first two bytes in memory are the tag.
        Span<byte> raw = stackalloc byte[16];
        Assert.True(FloatSub.TryWriteBytes(raw));
        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 }, raw.ToArray());
    }

    [Fact]
    public void Extensible_Float32Stereo_TypicalMixFormat()
    {
        var bytes = WaveFormatBytes.Extensible(FloatSub, 2, 48_000, 32, 32, channelMask: 0x3);
        Assert.Equal(40, bytes.Length);

        var format = WaveFormatParser.Parse(bytes);

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(SampleType.Float32, format.SampleType);
        Assert.Equal(8, format.BlockAlign);
        Assert.Equal(32, format.ValidBitsPerSample);
        Assert.Equal(0x3u, format.ChannelMask);
        Assert.True(format.IsExtensible);
        Assert.Equal(new AudioFormat(48_000, 2), format.AudioFormat);
    }

    [Fact]
    public void Extensible_Pcm24In32_IsPcm32LeftJustified()
    {
        var format = WaveFormatParser.Parse(WaveFormatBytes.Extensible(PcmSub, 2, 44_100, 32, 24));

        Assert.Equal(SampleType.Pcm32, format.SampleType);
        Assert.Equal(24, format.ValidBitsPerSample);
        Assert.Equal(8, format.BlockAlign);
    }

    [Fact]
    public void Extensible_Pcm16_SixChannels()
    {
        var format = WaveFormatParser.Parse(WaveFormatBytes.Extensible(PcmSub, 6, 48_000, 16, 16, channelMask: 0x3F));

        Assert.Equal(SampleType.Pcm16, format.SampleType);
        Assert.Equal(6, format.Channels);
        Assert.Equal(12, format.BlockAlign);
        Assert.Equal(0x3Fu, format.ChannelMask);
    }

    [Fact]
    public void Extensible_ZeroValidBits_MeansContainerBits()
    {
        var format = WaveFormatParser.Parse(WaveFormatBytes.Extensible(PcmSub, 1, 16_000, 24, 0));

        Assert.Equal(SampleType.Pcm24, format.SampleType);
        Assert.Equal(24, format.ValidBitsPerSample);
    }

    [Theory]
    [InlineData(16, (int)SampleType.Pcm16)]
    [InlineData(24, (int)SampleType.Pcm24)]
    [InlineData(32, (int)SampleType.Pcm32)]
    public void PlainPcm_Parses(int bits, int expected)
    {
        var format = WaveFormatParser.Parse(WaveFormatBytes.Ex(WaveFormatParser.WaveFormatPcm, 2, 48_000, bits));

        Assert.Equal((SampleType)expected, format.SampleType);
        Assert.False(format.IsExtensible);
        Assert.Equal(2 * bits / 8, format.BlockAlign);
    }

    [Fact]
    public void PlainIeeeFloat_Parses()
    {
        var format = WaveFormatParser.Parse(WaveFormatBytes.Ex(WaveFormatParser.WaveFormatIeeeFloat, 1, 96_000, 32));

        Assert.Equal(SampleType.Float32, format.SampleType);
        Assert.Equal(96_000, format.SampleRate);
    }

    [Fact]
    public void Rejects_ExtensibleWithShortCbSize()
    {
        var bytes = WaveFormatBytes.Extensible(FloatSub, 2, 48_000, 32, 32, cbSize: 0);

        Assert.False(WaveFormatParser.TryParse(bytes, out _, out var error));
        Assert.Contains("cbSize", error);
    }

    [Fact]
    public void Rejects_ExtensibleTruncated()
    {
        var bytes = WaveFormatBytes.Extensible(FloatSub, 2, 48_000, 32, 32);

        Assert.False(WaveFormatParser.TryParse(bytes.AsSpan(0, 30), out _, out _));
    }

    [Fact]
    public void Rejects_TruncatedHeader() =>
        Assert.False(WaveFormatParser.TryParse(new byte[17], out _, out _));

    [Fact]
    public void Rejects_UnknownSubFormat()
    {
        var bytes = WaveFormatBytes.Extensible(new Guid("00000092-0000-0010-8000-00aa00389b71"), 2, 48_000, 16, 16);

        Assert.False(WaveFormatParser.TryParse(bytes, out _, out var error));
        Assert.Contains("sub-format", error);
    }

    [Theory]
    [InlineData((ushort)0x0001, 8)]   // 8-bit PCM
    [InlineData((ushort)0x0003, 64)]  // float64
    [InlineData((ushort)0x0002, 4)]   // ADPCM tag
    public void Rejects_UnsupportedEncodings(ushort tag, int bits) =>
        Assert.False(WaveFormatParser.TryParse(WaveFormatBytes.Ex(tag, 2, 48_000, bits), out _, out _));

    [Fact]
    public void Rejects_BlockAlignMismatch()
    {
        var bytes = WaveFormatBytes.Ex(WaveFormatParser.WaveFormatPcm, 2, 48_000, 16, blockAlign: 6);

        Assert.False(WaveFormatParser.TryParse(bytes, out _, out var error));
        Assert.Contains("nBlockAlign", error);
    }

    [Fact]
    public void Rejects_ZeroChannelsOrRate()
    {
        Assert.False(WaveFormatParser.TryParse(WaveFormatBytes.Ex(WaveFormatParser.WaveFormatPcm, 0, 48_000, 16, blockAlign: 0), out _, out _));
        Assert.False(WaveFormatParser.TryParse(WaveFormatBytes.Ex(WaveFormatParser.WaveFormatPcm, 2, 0, 16), out _, out _));
    }

    [Fact]
    public void Rejects_ValidBitsAboveContainer() =>
        Assert.False(WaveFormatParser.TryParse(WaveFormatBytes.Extensible(PcmSub, 2, 48_000, 16, 24), out _, out _));

    [Fact]
    public void Parse_ThrowsNotSupported() =>
        Assert.Throws<NotSupportedException>(() => WaveFormatParser.Parse(WaveFormatBytes.Ex(WaveFormatParser.WaveFormatPcm, 2, 48_000, 8)));

    [Fact]
    public void GetTotalSize_IsHeaderPlusCbSize()
    {
        Assert.Equal(40, WaveFormatParser.GetTotalSize(WaveFormatBytes.Extensible(FloatSub, 2, 48_000, 32, 32)));
        Assert.Equal(18, WaveFormatParser.GetTotalSize(WaveFormatBytes.Ex(WaveFormatParser.WaveFormatPcm, 2, 48_000, 16)));
        Assert.Throws<ArgumentException>(() => WaveFormatParser.GetTotalSize(new byte[10]));
    }
}
