using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;
using Murmur.Core.Capture;

namespace Murmur.App.Tests;

/// <summary>The control that pulls the pictures of the screen recording: bitmap size, polling, allocations, pixels.</summary>
public sealed class VideoSurfaceTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static (Window Window, VideoSurface Surface) Show(FakeVideoPlayer player, double width = 480, bool active = true)
    {
        var surface = new VideoSurface { Player = player, Active = active };
        var window = new Window { Width = width, Height = 400, Content = new Border { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = surface }, Background = Brushes.White };
        window.Show();
        PumpFrames();
        return (window, surface);
    }

    /// <summary>
    /// Render ticks with a pause in between: the next animation frame is only handed out after the render thread
    /// reported the previous one done, which takes a moment of real time.
    /// </summary>
    private static void PumpFrames(int ticks = 3)
    {
        for (var i = 0; i < ticks; i++)
        {
            UiHarness.Pump(1);
            Thread.Sleep(1);
        }
    }

    private static async Task<FakeVideoPlayer> OpenedAsync(VideoSize? size = null)
    {
        var player = new FakeVideoPlayer(TimeSpan.FromSeconds(10), size);
        await player.OpenAsync("x.mp4", CancellationToken.None);
        return player;
    }

    [Fact]
    public async Task TheControl_IsSixteenByNine_AndTheBitmapFitsTheVideoIntoIt()
    {
        var wide = await OpenedAsync();
        var narrow = await OpenedAsync(new VideoSize(1024, 768));

        var (wideBitmap, wideBounds, narrowBitmap, narrowBounds, narrowPicture) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(wide);
                surface.CopyFrame();
                var first = (surface.BitmapSize, surface.Bounds.Size);
                window.Close();

                var (window2, surface2) = Show(narrow);
                surface2.CopyFrame();
                var second = (surface2.BitmapSize, surface2.Bounds.Size, surface2.PictureBounds);
                window2.Close();
                return (first.BitmapSize, first.Size, second.BitmapSize, second.Size, second.PictureBounds);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new Size(480, 270), wideBounds);
        Assert.Equal(new PixelSize(480, 270), wideBitmap);
        Assert.Equal(new Size(480, 270), narrowBounds);
        Assert.Equal(new PixelSize(360, 270), narrowBitmap); // letterboxed: pillars left and right
        Assert.Equal(new Rect(60, 0, 360, 270), narrowPicture);
        Assert.Equal(480, wide.LastWidth);
        Assert.Equal(480 * 4, wide.LastStride);
        Assert.Equal(360, narrow.LastWidth);
        Assert.Equal(270, narrow.LastHeight);
    }

    [Fact]
    public async Task FramesAreCopied_OnlyWhenThePlayerSaysThereIsANewPicture()
    {
        var player = await OpenedAsync();

        var (copied, builds, none, again) = await Session.Dispatch(
            () =>
            {
                // Shown inactive and activated without pumping: the render-priority poll timer must not take the opened
                // file's first picture before the test does (a pump lasts long enough for a tick on a coarse Windows clock).
                var (window, surface) = Show(player, active: false);
                surface.Active = true;
                var first = surface.CopyFrame(); // the opened file's first picture
                var noNews = Enumerable.Range(0, 5).Count(_ => surface.CopyFrame());
                player.NewPicture();
                var news = surface.CopyFrame();
                var noMore = surface.CopyFrame();
                var result = ((first ? 1 : 0) + (news ? 1 : 0), surface.BitmapBuilds, noNews + (noMore ? 1 : 0), surface.FramesCopied);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, copied);
        Assert.Equal(1, builds);
        Assert.Equal(0, none);
        Assert.Equal(2, again);
        Assert.Equal(again, player.Delivered);
    }

    [Fact]
    public async Task NothingIsPulled_WhileTheSurfaceIsNotActive()
    {
        var player = await OpenedAsync();

        var (calls, copied) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player, active: false);
                PumpFrames(10);
                var result = (player.CopyCalls, surface.CopyFrame());
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.False(copied);
    }

    [Fact]
    public async Task TheFrameLoop_RunsWhilePlaying_StopsWhenIdle_AndAwakesOnARevision()
    {
        var player = await OpenedAsync();

        var (started, playing, idle, woken, picture) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                var running = surface.IsLooping;
                player.IsPlaying = true;
                for (var i = 0; i < VideoSurface.IdleFrames + 40; i++)
                {
                    surface.Step();
                }

                var whilePlaying = surface.IsLooping;
                player.IsPlaying = false;
                for (var i = 0; i < VideoSurface.IdleFrames + 40; i++)
                {
                    surface.Step();
                }

                var stopped = !surface.IsLooping;

                // A seek (the view model bumps Revision) wakes it again and the picture arrives.
                surface.Revision++;
                var awake = surface.IsLooping;
                player.NewPicture();
                surface.Step();
                var delivered = player.Delivered;
                window.Close();
                return (running, whilePlaying, stopped, awake, delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.True(started);
        Assert.True(playing);
        Assert.True(idle);
        Assert.True(woken);
        Assert.Equal(2, picture); // the first picture of the opened file, then the one after the seek
    }

    [Fact]
    public async Task APlayerThatThrows_StopsTheLoop_InsteadOfEscapingTheTimerTick()
    {
        var player = await OpenedAsync();

        var (exception, looping, again) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                player.ThrowOnCopy = true;
                var thrown = Record.Exception(surface.Step);
                var stillLooping = surface.IsLooping;
                player.ThrowOnCopy = false;
                player.NewPicture();
                surface.Revision++; // the next seek wakes it and it works again
                var restarted = surface.IsLooping;
                window.Close();
                return (thrown, stillLooping, restarted);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(exception);
        Assert.False(looping);
        Assert.True(again);
    }

    [Fact]
    public async Task NoPicturesArePulled_WhileTheWindowIsMinimized()
    {
        var player = await OpenedAsync();

        var (calls, looping, resumed) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                window.WindowState = WindowState.Minimized;
                UiHarness.Pump(2);
                var before = player.CopyCalls;
                surface.Step();
                surface.Step();
                var result = (player.CopyCalls - before, surface.IsLooping, false);
                window.WindowState = WindowState.Normal;
                UiHarness.Pump(2);
                result.Item3 = surface.IsLooping;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.False(looping);
        Assert.True(resumed);
    }

    [Fact]
    public async Task TheStagingBuffer_OnlyGrows()
    {
        var player = await OpenedAsync();

        var (large, small) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                surface.CopyFrame();
                var big = surface.StagingLength;
                window.Width = 240;
                UiHarness.Pump(2);
                player.NewPicture();
                surface.CopyFrame();
                var result = (big, surface.StagingLength);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(480 * 270 * 4, large);
        Assert.Equal(large, small);
    }

    [Fact]
    public async Task ResizingTheControl_RecreatesTheBitmap_OnlyWhenTheSizeChanges()
    {
        var player = await OpenedAsync();

        var (first, second, third) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                surface.CopyFrame();
                var one = (surface.BitmapBuilds, surface.BitmapSize);
                for (var i = 0; i < 5; i++)
                {
                    player.NewPicture();
                    surface.CopyFrame();
                }

                var two = (surface.BitmapBuilds, surface.BitmapSize);
                window.Width = 320;
                PumpFrames();
                player.NewPicture();
                surface.CopyFrame();
                var three = (surface.BitmapBuilds, surface.BitmapSize);
                window.Close();
                return (one, two, three);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((1, new PixelSize(480, 270)), first);
        Assert.Equal(first, second);
        Assert.Equal(2, third.BitmapBuilds);
        Assert.Equal(new PixelSize(320, 180), third.BitmapSize);
    }

    [Fact]
    public async Task ThePictureIsDrawnIntoTheRenderedFrame_AndTheBarsKeepTheBackground()
    {
        var player = await OpenedAsync(new VideoSize(1024, 768));
        player.Color = 0xFF2040C0;

        var (centre, bar) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                player.NewPicture();
                surface.CopyFrame();
                PumpFrames(3);
                var result = (Pixel(window, 240, 135), Pixel(window, 20, 135));
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((0x20, 0x40, 0xC0), centre);
        Assert.Equal((255, 255, 255), bar);
    }

    [Fact]
    public async Task APollWithoutANewPicture_AllocatesNothing()
    {
        var player = await OpenedAsync();

        var (idle, busy, frames) = await Session.Dispatch(
            () =>
            {
                var (window, surface) = Show(player);
                player.NewPicture();
                surface.CopyFrame();
                for (var i = 0; i < 50; i++)
                {
                    surface.CopyFrame(); // warm-up
                }

                // Up to three passes, one of which must allocate nothing: a loaded machine can let the JIT's tiering or a
                // first-time runtime path land inside the measured loop once (like the other zero-allocation tests here).
                const int polls = 2000;
                long idleBytes = -1;
                for (var pass = 0; pass < 3 && idleBytes != 0; pass++)
                {
                    var start = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < polls; i++)
                    {
                        surface.CopyFrame();
                    }

                    idleBytes = GC.GetAllocatedBytesForCurrentThread() - start;
                }

                player.Continuous = true;
                for (var i = 0; i < 50; i++)
                {
                    surface.CopyFrame();
                }

                const int frameCount = 500;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < frameCount; i++)
                {
                    surface.CopyFrame();
                }

                var busyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                window.Close();
                return (idleBytes, busyBytes, frameCount);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, idle);
        Assert.True(busy / frames <= 256, $"{busy / frames} bytes per copied frame");
    }

    private static (int R, int G, int B) Pixel(Window window, int x, int y)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes) + (x * 4), pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (pixel[2], pixel[1], pixel[0]) : (pixel[0], pixel[1], pixel[2]);
    }
}
