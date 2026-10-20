using BenchmarkDotNet.Attributes;
using Murmur.Core.Playback;
using Murmur.Core.Sessions;

namespace Murmur.Benchmarks;

/// <summary>One UI tick of the screen/audio sync (runs every ~33 ms while a session plays): must stay allocation-free.</summary>
[MemoryDiagnoser]
public class ScreenPlaybackSyncBenchmarks
{
    private ScreenPlaybackSync _sync = null!;
    private TimeSpan _audio;

    [GlobalSetup]
    public void Setup()
    {
        var segments = new ScreenSegment[64];
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = new ScreenSegment(i, TimeSpan.FromMinutes(i), TimeSpan.FromSeconds(50));
        }

        _sync = new ScreenPlaybackSync(segments);
        _audio = TimeSpan.FromMinutes(31) + TimeSpan.FromSeconds(10);
    }

    [Benchmark]
    public VideoSyncCommand PlayingInSync() =>
        _sync.Update(_audio, audioPlaying: true, VideoPlayerState.Open(31, TimeSpan.FromSeconds(10), isPlaying: true, TimeSpan.FromSeconds(50)));

    [Benchmark]
    public VideoSyncCommand InAGap() =>
        _sync.Update(TimeSpan.FromMinutes(31) + TimeSpan.FromSeconds(55), audioPlaying: true, VideoPlayerState.Closed);
}
