using System.Diagnostics;
using Murmur.Capture.Windows.Video;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Writes the colour-coded <see cref="TestVideo"/> once for the class (lazily: nothing native runs off Windows).</summary>
public sealed class TestVideoFile : IDisposable
{
    private readonly Lock _gate = new();
    private string? _path;

    public string GetOrSkip()
    {
        var reason = TestVideo.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        lock (_gate)
        {
            if (_path is null)
            {
                var path = Path.Combine(Path.GetTempPath(), $"murmur-video-{Guid.NewGuid():N}.mp4");
                TestVideo.Write(path);
                _path = path;
            }

            return _path;
        }
    }

    public void Dispose()
    {
        if (_path is not null)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// The Media Engine player against a real file: open, play, pause, seek with the colour of the frame checked, the frame
/// copy contract, failures and clean-up. <see cref="VideoOutput.Auto"/> is what the app uses; the explicit outputs check
/// both pipelines where the machine has them (D3D11 is skipped with the reason where it cannot play, e.g. WARP).
/// </summary>
[Trait("Category", "Windows")]
public sealed class MediaEngineVideoPlayerTests(TestVideoFile video, ITestOutputHelper output) : IClassFixture<TestVideoFile>
{
    private const int FrameWidth = 160;
    private const int FrameHeight = 120;
    private const int FrameStride = FrameWidth * 4;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(nameof(VideoOutput.Auto))]
    [InlineData(nameof(VideoOutput.Wic))]
    [InlineData(nameof(VideoOutput.Direct3D))]
    public async Task Open_ReportsDurationAndSize_PausedAtZero(string requested)
    {
        using var player = await OpenAsync(Enum.Parse<VideoOutput>(requested));

        Assert.True(player.IsOpen);
        Assert.False(player.IsPlaying);
        Assert.Equal(new VideoSize(TestVideo.Width, TestVideo.Height), player.Size);
        Assert.InRange(player.Duration, TestVideo.Duration - TimeSpan.FromMilliseconds(100), TestVideo.Duration + TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Theory]
    [InlineData(nameof(VideoOutput.Auto))]
    [InlineData(nameof(VideoOutput.Wic))]
    [InlineData(nameof(VideoOutput.Direct3D))]
    public async Task Seek_ThenTryCopyFrame_ShowsTheFrameAtTheNewPosition(string requested)
    {
        using var player = await OpenAsync(Enum.Parse<VideoOutput>(requested));
        var frame = new byte[FrameStride * FrameHeight];

        // The first frame (slot 0), then seeks while paused, each into the middle of a slot.
        Assert.Equal(0, await WaitForSlotAsync(player, frame, 0));
        foreach (var seconds in new[] { 2.25, 0.75, 3.75, 1.25 })
        {
            var target = TimeSpan.FromSeconds(seconds);
            player.Seek(target);
            Assert.InRange(player.Position, target - TimeSpan.FromMilliseconds(40), target + TimeSpan.FromMilliseconds(40));
            Assert.True(TestVideo.SlotAt(target) == await WaitForSlotAsync(player, frame, TestVideo.SlotAt(target)), player.DescribeState());
        }

        Assert.False(player.IsPlaying);
    }

    [Theory]
    [InlineData(nameof(VideoOutput.Auto))]
    [InlineData(nameof(VideoOutput.Wic))]
    public async Task FastPausedSeeks_EachShowsItsFrame(string requested)
    {
        // Regression for the rare race where SEEKED arrives at once with the old picture and no new time stamp is ever
        // presented (BACKLOG, seen on the WARP runner); the self-recovery must show every target's slot.
        using var player = await OpenAsync(Enum.Parse<VideoOutput>(requested));
        var frame = new byte[FrameStride * FrameHeight];
        Assert.Equal(0, await WaitForSlotAsync(player, frame, 0));
        var slots = new[] { 2.25, 0.75, 3.75, 1.25, 3.25, 0.25, 2.75, 1.75 };
        for (var round = 0; round < 6; round++)
        {
            foreach (var seconds in slots)
            {
                var target = TimeSpan.FromSeconds(seconds);
                player.Seek(target);
                var expected = TestVideo.SlotAt(target);
                Assert.True(expected == await WaitForSlotAsync(player, frame, expected), $"round {round}, target {target}: {player.DescribeState()}");
            }
        }
    }

    [Fact]
    public async Task Play_AdvancesInRealTime_PauseHolds()
    {
        using var player = await OpenAsync(VideoOutput.Auto);
        var frame = new byte[FrameStride * FrameHeight];
        Assert.True(await WaitForFrameAsync(player, frame), player.DescribeState());

        // Like the session view: pull frames on every render tick while playing (frame-server mode).
        player.Play();
        var clock = Stopwatch.StartNew();
        var copies = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            copies += player.TryCopyFrame(frame, FrameWidth, FrameHeight, FrameStride) ? 1 : 0;
            await Task.Delay(16, TestContext.Current.CancellationToken);
        }

        var position = player.Position;
        var elapsed = clock.Elapsed;
        output.WriteLine($"after {elapsed.TotalMilliseconds:F0} ms: position {position.TotalMilliseconds:F0} ms, {copies} frames copied ({player.DeviceDescription})");
        output.WriteLine(player.DescribeState());
        Assert.True(player.IsPlaying, player.DescribeState());
        Assert.InRange(position, elapsed - TimeSpan.FromMilliseconds(300), elapsed + TimeSpan.FromMilliseconds(200));
        Assert.True(copies > 5, $"{copies} frames copied while playing; HRESULT 0x{player.LastTransferResult:X8}");

        player.Pause();
        Assert.False(player.IsPlaying);
        var paused = player.Position;
        await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);
        Assert.InRange(player.Position, paused - TimeSpan.FromMilliseconds(50), paused + TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Play_WithoutPullingFrames_DiagnosticsOnly()
    {
        // Not a contract (the view pulls frames every tick): records whether the engine's clock runs when nobody calls
        // OnVideoStreamTick, so a CI log answers it.
        using var player = await OpenAsync(VideoOutput.Auto);
        player.Play();
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        output.WriteLine($"position after 1 s of play without pulling frames: {player.Position.TotalMilliseconds:F0} ms; {player.DescribeState()}");
    }

    [Fact]
    public async Task TryCopyFrame_IsFalse_WhenNothingChanged_TrueForAnotherSize()
    {
        using var player = await OpenAsync(VideoOutput.Auto);
        var frame = new byte[FrameStride * FrameHeight];
        player.Seek(TimeSpan.FromSeconds(1.25));
        Assert.Equal(2, await WaitForSlotAsync(player, frame, 2));

        // Late engine events (SEEKED, LOADEDDATA, FIRSTFRAMEREADY) may still bring one more copy of the same picture:
        // drain until nothing has come for 200 ms.
        var quiet = Stopwatch.StartNew();
        var drain = Stopwatch.StartNew();
        while (quiet.Elapsed < TimeSpan.FromMilliseconds(200))
        {
            Assert.True(drain.Elapsed < Timeout, player.DescribeState());
            if (player.TryCopyFrame(frame, FrameWidth, FrameHeight, FrameStride))
            {
                quiet.Restart();
            }

            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        // Paused, nothing changes: false, and the buffer is left untouched.
        frame.AsSpan().Fill(0x5A);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            Assert.False(player.TryCopyFrame(frame, FrameWidth, FrameHeight, FrameStride), player.DescribeState());
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        Assert.All(frame, b => Assert.Equal(0x5A, b));

        // Another size: the same picture again, scaled.
        var large = new byte[TestVideo.Width * 4 * TestVideo.Height];
        Assert.True(player.TryCopyFrame(large, TestVideo.Width, TestVideo.Height, TestVideo.Width * 4), $"HRESULT 0x{player.LastTransferResult:X8}");
        Assert.Equal(2, TestVideo.CentreSlot(large, TestVideo.Width, TestVideo.Height, TestVideo.Width * 4).Slot);
        Assert.False(player.TryCopyFrame(large, TestVideo.Width, TestVideo.Height, TestVideo.Width * 4));
    }

    [Fact]
    public async Task TryCopyFrame_WhilePlaying_DoesNotAllocate()
    {
        using var player = await OpenAsync(VideoOutput.Auto);
        var frame = new byte[FrameStride * FrameHeight];
        player.Play();

        // Warm-up: the surfaces for this size and the first calls of every path. Warm-up and measuring must both fit in
        // the 4 s test file: Thread.Sleep(10) lasts ~15.6 ms on Windows, so 60 + 60 polls take about 2 s.
        var copies = 0;
        for (var i = 0; i < 60; i++)
        {
            copies += Poll(player, frame) ? 1 : 0;
            Thread.Sleep(10);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var measured = 0;
        for (var i = 0; i < 60; i++)
        {
            measured += Poll(player, frame) ? 1 : 0;
            Thread.Sleep(10);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{copies} frames copied while warming up, {measured} while measuring; {allocated} bytes allocated ({player.Output})");
        Assert.True(measured > 0, $"no frame copied while playing; HRESULT 0x{player.LastTransferResult:X8}");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task OpenAsync_CorruptFile_ThrowsOrRaisesFailed()
    {
        Skip();
        var path = Path.Combine(Path.GetTempPath(), $"murmur-corrupt-{Guid.NewGuid():N}.mp4");
        var bytes = new byte[64 * 1024];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        try
        {
            using var player = new MediaEngineVideoPlayer();
            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Failed += message => failed.TrySetResult(message);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(Timeout);
            try
            {
                await player.OpenAsync(path, cts.Token);
                var message = await failed.Task.WaitAsync(cts.Token);
                output.WriteLine($"Failed: {message}");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Runtime.InteropServices.COMException)
            {
                output.WriteLine($"{ex.GetType().Name}: {ex.Message}");
                Assert.False(player.IsOpen);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_Cancelled_ClosesThePlayer()
    {
        var path = video.GetOrSkip();
        using var player = new MediaEngineVideoPlayer();
        using var cts = new CancellationTokenSource();
        var open = player.OpenAsync(path, cts.Token);
        cts.Cancel();
        try
        {
            await open;
            Assert.True(player.IsOpen); // it was faster than the cancellation
        }
        catch (OperationCanceledException)
        {
            Assert.False(player.IsOpen);
        }

        // Still usable afterwards.
        await player.OpenAsync(path, TestContext.Current.CancellationToken);
        Assert.True(player.IsOpen);
    }

    [Fact]
    public async Task OpenAgain_ReplacesTheFile_CloseResets()
    {
        var path = video.GetOrSkip();
        using var player = new MediaEngineVideoPlayer();
        await player.OpenAsync(path, TestContext.Current.CancellationToken);
        player.Seek(TimeSpan.FromSeconds(2));
        await player.OpenAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.Zero, player.Position);

        player.Close();
        Assert.False(player.IsOpen);
        Assert.Equal(TimeSpan.Zero, player.Duration);
        Assert.Equal(default, player.Size);
        Assert.False(player.TryCopyFrame(new byte[FrameStride * FrameHeight], FrameWidth, FrameHeight, FrameStride));
        Assert.Throws<InvalidOperationException>(player.Play);
    }

    [Fact]
    public void WindowsVideoPlayback_IsSupported_WhereMediaFoundationIs()
    {
        Skip();
        var playback = new WindowsVideoPlayback();
        Assert.True(playback.IsSupported, playback.UnsupportedReason);
        Assert.True(((IVideoPlayback)new WindowsCapture()).IsSupported);
    }

    private static void Skip()
    {
        var reason = TestVideo.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
    }

    private static bool Poll(MediaEngineVideoPlayer player, byte[] frame)
    {
        _ = player.Position;
        _ = player.IsPlaying;
        return player.TryCopyFrame(frame, FrameWidth, FrameHeight, FrameStride);
    }

    private async Task<MediaEngineVideoPlayer> OpenAsync(VideoOutput requested)
    {
        var path = video.GetOrSkip();
        var gpu = MediaEngineVideoPlayer.GpuCapability;
        Assert.SkipWhen(requested == VideoOutput.Direct3D && !gpu.IsHardwareVideo, $"The D3D11 output needs a real GPU with video support; this machine has {gpu.Description}.");
        var player = new MediaEngineVideoPlayer(requested);
        var failures = new List<string>();
        player.Failed += message =>
        {
            lock (failures)
            {
                failures.Add(message);
            }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            await player.OpenAsync(path, cts.Token);
        }
        catch (Exception ex) when (requested == VideoOutput.Direct3D && ex is not OperationCanceledException)
        {
            player.Dispose();
            Assert.Skip($"The D3D11 output cannot play here ({ex.GetType().Name}: {ex.Message}).");
        }
        catch
        {
            player.Dispose();
            throw;
        }

        output.WriteLine($"opened with {player.Output} output ({player.DeviceDescription})");
        Assert.Equal(requested == VideoOutput.Auto ? player.Output : requested, player.Output);
        return player;
    }

    /// <summary>Polls TryCopyFrame until a frame of <paramref name="expected"/> arrives (or the timeout); returns the slot seen last.</summary>
    private async Task<int> WaitForSlotAsync(MediaEngineVideoPlayer player, byte[] frame, int expected)
    {
        var clock = Stopwatch.StartNew();
        var last = (Slot: -1, Distance: double.MaxValue);
        while (clock.Elapsed < Timeout)
        {
            if (player.TryCopyFrame(frame, FrameWidth, FrameHeight, FrameStride))
            {
                last = TestVideo.CentreSlot(frame, FrameWidth, FrameHeight, FrameStride);
                if (last.Slot == expected)
                {
                    output.WriteLine($"slot {expected} after {clock.ElapsedMilliseconds} ms (colour distance {last.Distance:F1})");
                    Assert.True(last.Distance < 80, $"colour distance {last.Distance:F1}");
                    return last.Slot;
                }
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        output.WriteLine($"timed out waiting for slot {expected}: last copied slot {last.Slot} (distance {last.Distance:F1}), last transfer HRESULT 0x{player.LastTransferResult:X8}, position {player.Position}");
        return last.Slot;
    }

    private async Task<bool> WaitForFrameAsync(MediaEngineVideoPlayer player, byte[] frame)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            if (player.TryCopyFrame(frame, FrameWidth, FrameHeight, FrameStride))
            {
                return true;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        output.WriteLine($"no frame within {Timeout}; last transfer HRESULT 0x{player.LastTransferResult:X8}");
        return false;
    }
}

/// <summary>Runs alone: the process handle count must not be moved by tests running in parallel.</summary>
[CollectionDefinition(nameof(HandleCountCollection), DisableParallelization = true)]
public sealed class HandleCountCollection;

/// <summary>Open/dispose cycles of the Media Engine player leave no handles behind.</summary>
[Trait("Category", "Windows")]
[Collection(nameof(HandleCountCollection))]
public sealed class MediaEngineHandleLeakTests(TestVideoFile video, ITestOutputHelper output) : IClassFixture<TestVideoFile>
{
    [Fact]
    public async Task OpenAndDispose_20Times_DoesNotLeakHandles()
    {
        var path = video.GetOrSkip();
        var frame = new byte[160 * 4 * 120];

        async Task Cycle()
        {
            using var player = new MediaEngineVideoPlayer();
            await player.OpenAsync(path, TestContext.Current.CancellationToken);
            var clock = Stopwatch.StartNew();
            while (!player.TryCopyFrame(frame, 160, 120, 160 * 4) && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        // Media Foundation's work queues, WIC and the D3D11 runtime start threads and keep handles after the first uses.
        for (var i = 0; i < 5; i++)
        {
            await Cycle();
        }

        var counts = new List<int> { HandleCount() };
        for (var i = 0; i < 20; i++)
        {
            await Cycle();
            counts.Add(HandleCount());
        }

        output.WriteLine($"handles after the warm-up and after each of 20 open/dispose cycles: {string.Join(", ", counts)}");
        Assert.InRange(counts[^1] - counts[0], int.MinValue, 30);
    }

    private static int HandleCount()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(200); // Media Foundation releases some objects and ends threads on its own threads
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.HandleCount;
    }
}
