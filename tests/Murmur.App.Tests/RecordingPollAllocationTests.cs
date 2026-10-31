using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;

namespace Murmur.App.Tests;

/// <summary>
/// The Recording screen polls its view model about four times a second for as long as a recording (or a pause) lasts, so
/// whatever one poll allocates is allocated 240 times a minute, for hours. A steady poll must cost nothing and a poll that
/// only moves the clock or a level must cost a few small strings, not a re-formatted screen.
/// </summary>
public sealed class RecordingPollAllocationTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly FakeRecorder _recorder = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSessionStore _store = new();

    public void Dispose() => _english.Dispose();

    private async Task<RecordingViewModel> RecordingAsync(bool paused = false)
    {
        var vm = new RecordingViewModel(_recorder, _capture, _store, action => action());
        await vm.StartStopAsync();
        if (paused)
        {
            await vm.PauseResumeCommand.ExecuteAsync(null);
        }

        return vm;
    }

    /// <summary>Bytes the calling thread allocated over <paramref name="polls"/> polls, after a warm-up (a pass of nothing is the steady state, a one-off JIT cost can land in any single pass).</summary>
    private static long Measure(Action<int> poll, int polls)
    {
        for (var i = 0; i < 50; i++)
        {
            poll(i);
        }

        long best = long.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < polls; i++)
            {
                poll(i);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            if (best == 0)
            {
                break;
            }
        }

        return best;
    }

    [Fact]
    public async Task ASteadyPoll_AllocatesNothing()
    {
        var vm = await RecordingAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(75);
        _recorder.Levels = new AudioLevels(0.126f, 0.063f);

        var bytes = Measure(_ => vm.Refresh(), 1000);

        Assert.True(bytes == 0, $"{bytes} bytes allocated by 1000 steady polls");
        Assert.Equal("00:01:15", vm.TimerText);
        Assert.Equal("−18 dB", vm.MicrophoneTrack.LevelText);
        Assert.Equal("Speakers · loopback", vm.SystemTrack.Detail);
    }

    [Fact]
    public async Task APausedPoll_AllocatesNothing()
    {
        var vm = await RecordingAsync(paused: true);
        _recorder.Elapsed = TimeSpan.FromSeconds(75);

        var bytes = Measure(_ => vm.Refresh(), 1000);

        Assert.True(bytes == 0, $"{bytes} bytes allocated by 1000 polls of a paused recording");
    }

    [Fact]
    public async Task ThePolls_WithinOneSecondAndOneDecibel_AllocateNothing()
    {
        var vm = await RecordingAsync();
        _recorder.Levels = new AudioLevels(0.126f, 0.063f);

        // Four polls per second of the same whole second, the level moving within the same rounded decibel.
        var bytes = Measure(
            i =>
            {
                _recorder.Elapsed = TimeSpan.FromMilliseconds(61_000 + ((i % 4) * 250));
                _recorder.Levels = new AudioLevels(0.126f + ((i % 3) * 0.0004f), 0.063f);
                vm.Refresh();
            },
            1000);

        Assert.True(bytes == 0, $"{bytes} bytes allocated by 1000 polls within one second and one decibel");
    }

    [Fact]
    public async Task APollThatMovesTheClock_AllocatesOnlyTheNewText()
    {
        var vm = await RecordingAsync();
        _recorder.Levels = new AudioLevels(0.126f, 0.063f);
        const int seconds = 400;

        var bytes = Measure(
            i =>
            {
                _recorder.Elapsed = TimeSpan.FromMilliseconds(i * 250.0);
                vm.Refresh();
            },
            seconds * 4);

        // One "HH:MM:SS" string a second (a few dozen bytes), not one per poll plus a formatted level and device name.
        Assert.True(bytes <= seconds * 120L, $"{bytes} bytes for {seconds} s of polls ({bytes / (double)seconds:F0} B per second)");
    }

    [Fact]
    public async Task TheCachedTexts_FollowTheLevel_NotJustTheFirstPoll()
    {
        var vm = await RecordingAsync();
        vm.Refresh();
        Assert.Equal("Speakers · loopback", vm.SystemTrack.Detail);

        _recorder.Levels = new AudioLevels(0.126f, 0f);
        vm.Refresh();
        Assert.Equal("−18 dB", vm.MicrophoneTrack.LevelText);
        _recorder.Levels = new AudioLevels(0.5f, 0f);
        vm.Refresh();
        Assert.Equal("−6 dB", vm.MicrophoneTrack.LevelText);
    }

    [Fact]
    public async Task TheClockText_StartsFromZeroAgain_AfterAStop()
    {
        var vm = await RecordingAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(3);
        vm.Refresh();
        Assert.Equal("00:00:03", vm.TimerText);

        await vm.StartStopAsync();
        Assert.Equal("00:00:00", vm.TimerText);

        // A new recording whose first poll is at the very second the last one showed.
        await vm.StartStopAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(3);
        vm.Refresh();
        Assert.Equal("00:00:03", vm.TimerText);
    }
}
