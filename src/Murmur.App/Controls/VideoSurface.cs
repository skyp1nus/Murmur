using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.Core.Capture;

namespace Murmur.App.Controls;

/// <summary>
/// The 16:9 picture of the screen recording in the session view (decision D-035: the player draws nothing itself, this
/// control pulls the current picture from <see cref="IVideoPlayer.TryCopyFrame"/> into its own bitmap).
/// </summary>
/// <remarks>
/// <para><b>Bitmap.</b> One <see cref="WriteableBitmap"/> (BGRA8888, opaque) the size of the video fitted into the control
/// (letterboxed, in device pixels: the render scaling is applied), recreated only when that size, the render scaling or
/// the native size of the video changes. It is drawn 1:1, so the picture is not resampled a second time.</para>
/// <para><b>Frames.</b> While <see cref="Active"/> (a segment is open) and there is something to see, a render-priority
/// timer (<see cref="FrameInterval"/>, about 60 Hz) calls <see cref="CopyFrame"/>. A timer rather than
/// <c>TopLevel.RequestAnimationFrame</c>: the animation-frame clock only keeps ticking while something invalidates the
/// window every frame, and this control invalidates only when a picture is new. The loop runs while the video plays, and for
/// <see cref="IdleFrames"/> polls after every <see cref="Revision"/> change or copied frame (a seek on a paused video
/// shows its picture some milliseconds later); then it stops, so a paused session view does no per-frame work. The
/// player is only polled: when <see cref="IVideoPlayer.TryCopyFrame"/> says there is no new picture the bitmap is left
/// alone and nothing is invalidated.</para>
/// <para><b>Allocations.</b> A poll allocates nothing: the timer and its handler are created once, nothing is formatted
/// or captured, and the player writes into a staging buffer that is kept for the size. Only a picture that is really new
/// is copied on into the locked framebuffer of the bitmap (the platform allocates its lock object then, at most once per
/// video frame). Asking the bitmap for the framebuffer on every poll instead would allocate on every poll.</para>
/// <para><b>Layout.</b> The control is 16:9 for the width it gets; the parent clips the rounded corners and puts the
/// placeholder and the chip over it. Colours: the background is the parent's, nothing is hard-coded here.</para>
/// </remarks>
public sealed class VideoSurface : Control
{
    /// <summary>The player to pull pictures from; owned (and disposed) by the session view model.</summary>
    public static readonly StyledProperty<IVideoPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoSurface, IVideoPlayer?>(nameof(Player));

    /// <summary>True while a segment is open in <see cref="Player"/>; no pictures are pulled otherwise.</summary>
    public static readonly StyledProperty<bool> ActiveProperty =
        AvaloniaProperty.Register<VideoSurface, bool>(nameof(Active));

    /// <summary>Bumped by the view model whenever it seeked, played or paused the video: wakes the frame loop.</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<VideoSurface, int>(nameof(Revision));

    /// <summary>Time between two polls of the player (about 60 Hz).</summary>
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>Polls the loop keeps going after the last change while the video is not playing (about 3 s).</summary>
    internal const int IdleFrames = 180;

    /// <summary>The video's aspect ratio, 16:9, is the panel's of the design.</summary>
    private const double AspectWidth = 16;
    private const double AspectHeight = 9;

    private const int BytesPerPixel = 4;

    private readonly DispatcherTimer _timer;
    private byte[] _staging = [];
    private TopLevel? _topLevel;
    private Window? _window;
    private WriteableBitmap? _bitmap;
    private PixelSize _bitmapSize;
    private double _bitmapScaling;
    private VideoSize _native;
    private Size _bounds;
    private Rect _destination;
    private bool _hasPicture;
    private int _idle;

    static VideoSurface()
    {
        AffectsRender<VideoSurface>(ActiveProperty);
        PlayerProperty.Changed.AddClassHandler<VideoSurface>(static (surface, _) => surface.OnPlayerChanged());
        ActiveProperty.Changed.AddClassHandler<VideoSurface>(static (surface, _) => surface.OnActiveChanged());
        RevisionProperty.Changed.AddClassHandler<VideoSurface>(static (surface, _) => surface.Wake());
    }

    public VideoSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += OnTimerTick;
    }

    public IVideoPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public bool Active
    {
        get => GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>Size of the current bitmap in pixels (zero before the first picture); for tests.</summary>
    internal PixelSize BitmapSize => _bitmapSize;

    /// <summary>Length of the staging buffer (for tests: it only grows).</summary>
    internal int StagingLength => _staging.Length;

    /// <summary>How many bitmaps were created (for tests: must not grow with the frames).</summary>
    internal int BitmapBuilds { get; private set; }

    /// <summary>How many pictures were copied into the bitmap (for tests).</summary>
    internal int FramesCopied { get; private set; }

    /// <summary>True while the poll timer runs (for tests).</summary>
    internal bool IsLooping => _timer.IsEnabled;

    /// <summary>Where the picture is drawn, in device-independent pixels (for tests).</summary>
    internal Rect PictureBounds => _destination;

    /// <summary>The 16:9 height for the width the control gets; no height when the width is not known.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width))
        {
            return default;
        }

        return new Size(availableSize.Width, availableSize.Width * AspectHeight / AspectWidth);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _window = _topLevel as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        Wake();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        _topLevel = null;
        _timer.Stop();
        DropPicture();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Wake();
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is { } bitmap && _hasPicture && Active)
        {
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), _destination);
        }
    }

    /// <summary>
    /// Pulls the current picture of the player into the bitmap and invalidates the control when there was a new one.
    /// One animation-frame step; public to the tests.
    /// </summary>
    /// <returns>True when a new picture was copied.</returns>
    internal bool CopyFrame()
    {
        if (!CanShow || Player is not { } player)
        {
            return false;
        }

        try
        {
            var native = player.Size;
            if (native.Width <= 0 || native.Height <= 0 || !EnsureBitmap(native) || _bitmap is not { } bitmap)
            {
                return false;
            }

            // The player writes into a managed staging buffer first: asking the bitmap for its framebuffer on every poll
            // would allocate the platform's lock object 60 times a second, also when there is nothing new to show.
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            var stride = width * BytesPerPixel;
            var length = stride * height;
            if (_staging.Length < length)
            {
                // Only ever grows: a window dragged smaller reuses the buffer.
                _staging = new byte[length];
            }

            var copied = player.TryCopyFrame(_staging.AsSpan(0, length), width, height, stride);
            if (copied)
            {
                using (var framebuffer = bitmap.Lock())
                {
                    if (framebuffer.RowBytes == stride)
                    {
                        Marshal.Copy(_staging, 0, framebuffer.Address, length);
                    }
                    else
                    {
                        for (var y = 0; y < height; y++)
                        {
                            Marshal.Copy(_staging, y * stride, framebuffer.Address + (y * framebuffer.RowBytes), stride);
                        }
                    }
                }

                FramesCopied++;
                _hasPicture = true;
                InvalidateVisual();
            }

            return copied;
        }
        catch (ObjectDisposedException)
        {
            // The view model disposed the player a moment before it cleared the property.
            return false;
        }
    }

    /// <summary>(Re)creates the bitmap when the fitted size changed; false when there is no room for a picture.</summary>
    private bool EnsureBitmap(VideoSize native)
    {
        var bounds = Bounds.Size;
        var scaling = _topLevel?.RenderScaling ?? 1.0;
        if (_bitmap is not null && native == _native && bounds == _bounds && scaling == _bitmapScaling)
        {
            return true;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0 || scaling <= 0)
        {
            return false;
        }

        // Fit the native aspect ratio into the control, in device pixels.
        var aspect = (double)native.Width / native.Height;
        var boxWidth = bounds.Width * scaling;
        var boxHeight = bounds.Height * scaling;
        var width = boxWidth / boxHeight > aspect ? boxHeight * aspect : boxWidth;
        var height = boxWidth / boxHeight > aspect ? boxHeight : boxWidth / aspect;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
        var left = Math.Floor((boxWidth - pixels.Width) / 2);
        var top = Math.Floor((boxHeight - pixels.Height) / 2);

        _native = native;
        _bounds = bounds;
        _bitmapScaling = scaling;
        _destination = new Rect(left / scaling, top / scaling, pixels.Width / scaling, pixels.Height / scaling);
        if (_bitmap is null || pixels != _bitmapSize)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(pixels, new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _bitmapSize = pixels;
            BitmapBuilds++;

            // The new bitmap is blank and the player believes it delivered its picture: it must repaint for the new size
            // (the Media Engine does: a changed size makes a picture due), so show nothing until it does.
            _hasPicture = false;
        }

        InvalidateVisual();
        return true;
    }

    private void OnPlayerChanged()
    {
        DropPicture();
        Wake();
    }

    private void OnActiveChanged()
    {
        if (!Active)
        {
            _hasPicture = false;
        }

        Wake();
    }

    private void DropPicture()
    {
        _hasPicture = false;
        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapSize = default;
        _native = default;
        _bounds = default;
    }

    /// <summary>Starts (or prolongs) the frame loop.</summary>
    private void Wake()
    {
        _idle = 0;
        Schedule();
    }

    private void Schedule()
    {
        if (_timer.IsEnabled || Player is null || !CanShow)
        {
            return;
        }

        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e) => Step();

    /// <summary>One poll of the loop: pulls a picture, and stops the timer when nothing more is expected.</summary>
    internal void Step()
    {
        try
        {
            if (CopyFrame())
            {
                _idle = 0;
            }
            else
            {
                _idle++;
            }
        }
        catch (Exception)
        {
            // A decoder or the platform failed: an exception escaping a timer tick would end the app (and the audio with
            // it). Stop polling and show no picture; the player's own Failed event tells the view model.
            _timer.Stop();
            _hasPicture = false;
            InvalidateVisual();
            return;
        }

        if (!CanShow || Player is not { } player || (_idle >= IdleFrames && !SafeIsPlaying(player)))
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Pictures are only pulled for a segment that is open, in a control that is on screen: not while the window is
    /// minimized or the session view is hidden. (A hidden ancestor has no size: showing it again resizes the control,
    /// which wakes the loop.)
    /// </summary>
    private bool CanShow => Active && _topLevel is not null && IsEffectivelyVisible && _window?.WindowState != WindowState.Minimized;

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Wake();
        }
    }

    private static bool SafeIsPlaying(IVideoPlayer player)
    {
        try
        {
            return player.IsPlaying;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
