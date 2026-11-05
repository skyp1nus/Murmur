using Murmur.Core.Import;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Import;

public sealed class CompositeMediaDecoderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Extensions_AreTheUnionOfTheDecoders()
    {
        var composite = new CompositeMediaDecoder(new FakeMediaDecoder(".mp3", ".mp4"), new WavMediaDecoder());

        Assert.True(composite.Extensions.SetEquals([".mp3", ".mp4", ".wav"]));
    }

    [Fact]
    public async Task AFile_GoesToTheFirstDecoderThatTakesItsExtension_CaseInsensitively()
    {
        var first = new FakeMediaDecoder(".mp3");
        var second = new FakeMediaDecoder(".mp3", ".flac");
        var composite = new CompositeMediaDecoder(first, second);
        var mp3 = _dir.File("Talk.MP3");
        var flac = _dir.File("talk.flac");
        await File.WriteAllBytesAsync(mp3, [1, 2, 3], Ct);
        await File.WriteAllBytesAsync(flac, [1, 2, 3], Ct);

        await composite.ProbeAsync(mp3, Ct);
        await composite.DecodeAudioAsync(flac, _dir.File("out.wav"), null, Ct);

        Assert.Equal(1, first.Probes);
        Assert.Equal(0, second.Probes);
        Assert.Equal([flac], second.Decoded);
        Assert.Empty(first.Decoded);
    }

    [Fact]
    public async Task AnUnknownExtension_IsNotSupported()
    {
        var composite = new CompositeMediaDecoder(new WavMediaDecoder());

        await Assert.ThrowsAsync<NotSupportedException>(async () => await composite.ProbeAsync(_dir.File("notes.txt"), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await composite.DecodeAudioAsync(_dir.File("clip"), _dir.File("o.wav"), null, Ct));
    }
}
