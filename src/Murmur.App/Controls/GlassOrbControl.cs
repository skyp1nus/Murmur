using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The orb of the onboarding canvas (the wordmark, the welcome orb and the voice orb): a glass sphere painted with the
/// <c>orbGradient</c> of <c>docs/design/tokens.json</c> (a lavender base with soft pink, ember, blue and highlight blobs), an
/// inner white glow along its rim and, behind it, the same sphere blurred into a halo. Like <see cref="OrbDisc"/> it reads
/// the gradient from the generated <c>Orb.*</c> resources (the base, and per layer the colour, centre and fade-out), so
/// <c>orbGradient</c> in tokens.json is the single source of both orbs; the control is the only place besides the other orb
/// and the icons where the accent colours appear.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drawing happens once.</b> The sphere and the halo are two layers; each repaints only when its size, colours or look
/// change. Animation only sets <c>Scale</c> and <c>Opacity</c> of the layers' composition visuals (no redraw, no allocation).
/// </para>
/// <para>
/// <b>Animation.</b> Not <see cref="IsActive"/>: one static frame, no timer. Active without a <see cref="LevelSource"/>: the
/// canvas "breathe" (3.6 s, the sphere grows 5 %, the halo 16 % and fades). Active with a source: the sphere and halo follow
/// the voice level. A 15 Hz timer drives it, never the 60 Hz animation-frame loop. While <see cref="Motion.ReduceMotionProperty"/>
/// is on, the breathing and the voice pulse are decoration the user asked to do without: the orb holds its rest frame and the
/// timer is stopped.
/// </para>
/// </remarks>
public sealed class GlassOrbControl : Panel
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<GlassOrbControl, bool>(nameof(IsActive));

    public static readonly StyledProperty<ILevelSource?> LevelSourceProperty =
        AvaloniaProperty.Register<GlassOrbControl, ILevelSource?>(nameof(LevelSource));

    /// <summary>The sphere's diameter as a share of the control (the canvas: 232 of 300, 132 of 168, 18 of 18).</summary>
    public static readonly StyledProperty<double> BodyFractionProperty =
        AvaloniaProperty.Register<GlassOrbControl, double>(nameof(BodyFraction), 1.0);

    /// <summary>Standard deviation of the halo's Gaussian blur in px (CSS <c>blur()</c>); 0 = no halo (the wordmark).</summary>
    public static readonly StyledProperty<double> HaloBlurProperty =
        AvaloniaProperty.Register<GlassOrbControl, double>(nameof(HaloBlur));

    /// <summary>Opacity of the halo at rest.</summary>
    public static readonly StyledProperty<double> HaloOpacityProperty =
        AvaloniaProperty.Register<GlassOrbControl, double>(nameof(HaloOpacity), 1.0);

    /// <summary>Blur of the white inner glow in px (the canvas' <c>box-shadow: inset 0 0 40px</c>); 0 = none.</summary>
    public static readonly StyledProperty<double> InnerGlowBlurProperty =
        AvaloniaProperty.Register<GlassOrbControl, double>(nameof(InnerGlowBlur));

    /// <summary>Opacity of the white inner glow.</summary>
    public static readonly StyledProperty<double> InnerGlowOpacityProperty =
        AvaloniaProperty.Register<GlassOrbControl, double>(nameof(InnerGlowOpacity), 0.35);

    private const double BreathPeriodSeconds = 3.6;
    private const double BreathBodyGrowth = 0.05;
    private const double BreathHaloGrowth = 0.16;
    private const double BreathHaloFade = 0.6;
    private const double VoiceBodyGrowth = 0.09;
    private const double VoiceHaloGrowth = 0.2;
    private const double AttackPerSecond = 14.0;
    private const double ReleasePerSecond = 3.5;
    private const double MaxStepSeconds = 0.1;
    private const double MinUpdateSeconds = 0.066;

    // A timer tick is never exactly one interval after the last: ticks a little early still count, or every other tick would be dropped.
    private const double UpdateSlack = 0.8;
    private const double SkipThreshold = 0.003;

    /// <summary>How many halo bitmaps this orb computed (for tests: once per size, blur and theme colours).</summary>
    internal int HaloBuilds { get; private set; }

    private readonly Layer _halo;
    private readonly Layer _body;
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(MinUpdateSeconds);

    // While the orb is hidden or its window minimized nothing is drawn: the timer only wakes up twice a second to notice the change.
    private static readonly TimeSpan HiddenInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;
    private TimeProvider _clock = TimeProvider.System;
    private long _clockStart;
    private bool _primed;
    private TimeSpan _lastFrame;
    private double _smoothed;
    private double _phaseSeconds;
    private double _appliedBody = 1;
    private double _appliedHalo = 1;
    private double _appliedFade;
    private long _appliedFrames;
    private Size _size;
    private Palette _palette;
    private bool _hasColours;
    private bool _easeInFromRest;
    private bool _centreKnown;

    static GlassOrbControl()
    {
        // Only the layers a property shows in repaint; the halo bitmap is never dropped here, its cache key decides when it is rebuilt.
        BodyFractionProperty.Changed.AddClassHandler<GlassOrbControl>((o, _) => o.OnLookChanged(layout: true, halo: false, body: true));
        HaloBlurProperty.Changed.AddClassHandler<GlassOrbControl>((o, _) => o.OnLookChanged(layout: true, halo: true, body: false));
        HaloOpacityProperty.Changed.AddClassHandler<GlassOrbControl>((o, _) => o.OnLookChanged(layout: false, halo: true, body: false));
        InnerGlowBlurProperty.Changed.AddClassHandler<GlassOrbControl>((o, _) => o.OnLookChanged(layout: false, halo: false, body: true));
        InnerGlowOpacityProperty.Changed.AddClassHandler<GlassOrbControl>((o, _) => o.OnLookChanged(layout: false, halo: false, body: true));
    }

    public GlassOrbControl()
    {
        _halo = new Layer(this, isHalo: true) { IsHitTestVisible = false };
        _body = new Layer(this, isHalo: false) { IsHitTestVisible = false };
        _clockStart = _clock.GetTimestamp();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = ActiveInterval };
        _timer.Tick += (_, _) => OnTimer();
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        Children.Add(_halo);
        Children.Add(_body);
        ActualThemeVariantChanged += (_, _) => ResolveColours();
    }

    /// <summary>True while the orb moves: it breathes, or follows <see cref="LevelSource"/> when there is one.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Where the orb reads the voice level from; <c>null</c> = the canvas breathing.</summary>
    public ILevelSource? LevelSource
    {
        get => GetValue(LevelSourceProperty);
        set => SetValue(LevelSourceProperty, value);
    }

    public double BodyFraction
    {
        get => GetValue(BodyFractionProperty);
        set => SetValue(BodyFractionProperty, value);
    }

    public double HaloBlur
    {
        get => GetValue(HaloBlurProperty);
        set => SetValue(HaloBlurProperty, value);
    }

    public double HaloOpacity
    {
        get => GetValue(HaloOpacityProperty);
        set => SetValue(HaloOpacityProperty, value);
    }

    public double InnerGlowBlur
    {
        get => GetValue(InnerGlowBlurProperty);
        set => SetValue(InnerGlowBlurProperty, value);
    }

    public double InnerGlowOpacity
    {
        get => GetValue(InnerGlowOpacityProperty);
        set => SetValue(InnerGlowOpacityProperty, value);
    }

    /// <summary>Where the halo layer sits relative to the control (for tests).</summary>
    internal Rect HaloBounds => _halo.Bounds;

    /// <summary>True while the halo layer holds its computed bitmap (for tests).</summary>
    internal bool HasHaloBitmap => _halo.HasBitmap;

    /// <summary>True while the system asks for less motion (inherited from the window): the orb holds its rest frame.</summary>
    private bool ReduceMotion => Motion.GetReduceMotion(this);

    /// <summary>True while the control runs its update timer (for tests).</summary>
    internal bool IsFrameLoopRunning => _timer.IsEnabled;

    /// <summary>How many frames were pushed to the composition visuals (for tests).</summary>
    internal long AppliedFrames => _appliedFrames;

    /// <summary>Current composition scale of the sphere (for tests); 0 when not attached.</summary>
    internal double BodyScale => ElementComposition.GetElementVisual(_body)?.Scale.X ?? 0;

    /// <summary>Current composition scale of the halo (for tests); 0 when not attached.</summary>
    internal double HaloScale => ElementComposition.GetElementVisual(_halo)?.Scale.X ?? 0;

    /// <summary>
    /// The clock every update reads; the system clock by default. Replacing it restarts the animation (internal: tests set a
    /// manual clock to render the same frame on every run).
    /// </summary>
    internal TimeProvider Clock
    {
        get => _clock;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _clock = value;
            _clockStart = value.GetTimestamp();
            _primed = false;
        }
    }

    /// <summary>One timer tick: a step when the orb can be seen, otherwise the timer slows down until it can.</summary>
    internal void OnTimer()
    {
        var shown = IsEffectivelyVisible && (TopLevel.GetTopLevel(this) as Window)?.WindowState != WindowState.Minimized;
        var wanted = shown ? ActiveInterval : HiddenInterval;
        if (_timer.Interval != wanted)
        {
            _timer.Interval = wanted;
            _primed = false;
        }

        if (shown)
        {
            Tick();
        }
    }

    /// <summary>The timer's current interval (for tests).</summary>
    internal TimeSpan TimerInterval => _timer.Interval;

    /// <summary>One animation step at the clock's current time: exactly what the timer does (internal for tests).</summary>
    internal void Tick() => Update(_clock.GetElapsedTime(_clockStart));

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);
        var size = new Size(side, side);
        _halo.Measure(new Rect(size).Inflate(Math.Ceiling(HaloBlur * 3)).Size);
        _body.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _size = finalSize;
        var rect = new Rect(finalSize);

        // An effect is clipped to the bounds of its control: the halo layer reaches three standard deviations past the orb.
        var pad = Math.Ceiling(HaloBlur * 3);
        _halo.Arrange(rect.Inflate(pad));
        _body.Arrange(rect);
        _centreKnown = false;
        PushFrame();
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _centreKnown = false;
        ResolveColours();
        PushFrame();
        UpdateLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _primed = false;
        _timer.Stop();

        // A detached orb holds no bitmap; attaching it again draws (and builds) the halo anew.
        _halo.ReleaseHalo();
        _centreKnown = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            _easeInFromRest = false;
            _primed = false;
            if (!IsActive)
            {
                HoldRest();
            }

            UpdateLoop();
        }
        else if (change.Property == Motion.ReduceMotionProperty)
        {
            _primed = false;
            if (ReduceMotion)
            {
                HoldRest();
            }
            else
            {
                // Motion is allowed again mid-recording: the pulse eases in from rest instead of snapping to the current level.
                _easeInFromRest = IsActive;
            }

            UpdateLoop();
        }
    }

    /// <summary>Shows the rest frame (the canvas' static orb) and forgets the running animation.</summary>
    private void HoldRest()
    {
        _smoothed = 0;
        Apply(1, 1, 0);
    }

    private void OnLookChanged(bool layout, bool halo, bool body)
    {
        if (halo)
        {
            _halo.InvalidateLook();
        }

        if (body)
        {
            _body.InvalidateLook();
        }

        if (layout)
        {
            InvalidateMeasure();
        }

        PushFrame();
    }

    private void UpdateLoop()
    {
        var run = IsActive && !ReduceMotion && ((Visual)this).IsAttachedToVisualTree();
        if (run && !_timer.IsEnabled)
        {
            _primed = false;
            _timer.Interval = ActiveInterval;
            _timer.Start();
        }
        else if (!run && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    /// <summary>One animation step at <paramref name="timestamp"/> on the orb's clock; a step too soon after the last changes nothing.</summary>
    internal void Update(TimeSpan timestamp)
    {
        if (ReduceMotion)
        {
            return;
        }

        var elapsed = (timestamp - _lastFrame).TotalSeconds;
        if (_primed && elapsed < MinUpdateSeconds * UpdateSlack)
        {
            return;
        }

        var starting = !_primed;
        var easeIn = starting && _easeInFromRest;
        if (starting)
        {
            _easeInFromRest = false;
        }

        var source = LevelSource;
        if (source is null)
        {
            // The canvas "breathe": ease in and out between rest (0) and the peak (1) every 3.6 s, starting at rest.
            _primed = true;
            _lastFrame = timestamp;
            _phaseSeconds = starting ? 0 : _phaseSeconds + Math.Min(elapsed, MaxStepSeconds);
            var breath = 0.5 - (0.5 * Math.Cos(_phaseSeconds * (2 * Math.PI / BreathPeriodSeconds)));
            if (starting || Math.Abs(breath - _smoothed) >= SkipThreshold)
            {
                _smoothed = breath;
                Apply(1 + (BreathBodyGrowth * breath), 1 + (BreathHaloGrowth * breath), BreathHaloFade * breath);
            }

            return;
        }

        var level = source.Level;
        var target = level > 0f ? Math.Sqrt(Math.Min(level, 1f)) : 0;
        if (starting)
        {
            _primed = true;
            _smoothed = easeIn ? 0 : target;
        }
        else
        {
            var dt = Math.Min(elapsed, MaxStepSeconds);
            var rate = target > _smoothed ? AttackPerSecond : ReleasePerSecond;
            _smoothed += (target - _smoothed) * (1 - Math.Exp(-rate * dt));
        }

        _lastFrame = timestamp;
        if (starting || Math.Abs(_smoothed - _appliedFade / BreathHaloFade) >= SkipThreshold * 4)
        {
            Apply(1 + (VoiceBodyGrowth * _smoothed), 1 + (VoiceHaloGrowth * _smoothed), BreathHaloFade * _smoothed);
        }
    }

    private void Apply(double bodyScale, double haloScale, double haloFade)
    {
        _appliedBody = bodyScale;
        _appliedHalo = haloScale;
        _appliedFade = haloFade;
        PushFrame();
    }

    /// <summary>Pushes the frame on screen to the composition visuals: scales both layers about the centre and fades the halo.</summary>
    private void PushFrame()
    {
        _appliedFrames++;

        // The centre points only change when the control is arranged or attached: the animation sets just the scale and the opacity.
        var setCentres = !_centreKnown;
        var centre = new Vector3D(_size.Width / 2, _size.Height / 2, 0);
        if (ElementComposition.GetElementVisual(_body) is { } body)
        {
            if (setCentres)
            {
                body.CenterPoint = centre;
            }

            body.Scale = new Vector3D(_appliedBody, _appliedBody, 1);
        }

        if (ElementComposition.GetElementVisual(_halo) is { } halo)
        {
            if (setCentres)
            {
                var pad = Math.Ceiling(HaloBlur * 3);
                halo.CenterPoint = new Vector3D(centre.X + pad, centre.Y + pad, 0);
            }

            halo.Scale = new Vector3D(_appliedHalo, _appliedHalo, 1);
            halo.Opacity = (float)(1 - _appliedFade);
        }

        // Both visuals exist (or neither does yet): only then is the centre pushed for good.
        _centreKnown = ElementComposition.GetElementVisual(_body) is not null && ElementComposition.GetElementVisual(_halo) is not null;
    }

    /// <summary>Reads the gradient from the <c>Orb.*</c> resources of the current theme; an orb whose resources are missing paints nothing.</summary>
    private void ResolveColours()
    {
        if (Lookup("Orb.Base", out Color baseColour)
            && ResolveLayer("Cream", out var cream) && ResolveLayer("Violet", out var violet) && ResolveLayer("Ember", out var ember) && ResolveLayer("Pink", out var pink))
        {
            _palette = new Palette(baseColour, pink, ember, violet, cream);
            _hasColours = true;
        }
        else
        {
            _hasColours = false;
        }

        // The bodies repaint with the new colours; the halo's bitmap is rebuilt only if its key (which has the palette) really changed.
        _halo.InvalidateLook();
        _body.InvalidateLook();
    }

    private bool ResolveLayer(string name, out Blob blob)
    {
        if (Lookup("Orb." + name, out Color colour) && Lookup("Orb." + name + "X", out double x)
            && Lookup("Orb." + name + "Y", out double y) && Lookup("Orb." + name + "Stop", out double stop))
        {
            blob = new Blob(colour, x, y, stop);
            return true;
        }

        blob = default;
        return false;
    }

    private bool Lookup<T>(string key, out T value)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var found) && found is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>One CSS gradient layer: colour, centre in the element (0..1), the stop (share of the farthest corner) where it fades out.</summary>
    private readonly record struct Blob(Color Colour, double X, double Y, double Reach);

    /// <summary>
    /// The orb gradient of tokens.json <c>orbGradient</c> from the bottom up (CSS lists the layers top first: cream highlight, violet,
    /// ember, pink, over the lavender base): the one description both the sphere's brushes and the halo's bitmap are made from. It has
    /// value equality, so it is part of the halo's cache key and a change of the colours rebuilds the bitmap.
    /// </summary>
    private readonly record struct Palette(Color Base, Blob Pink, Blob Ember, Blob Violet, Blob Cream)
    {
        public Blob[] Blobs() => [Pink, Ember, Violet, Cream];
    }

    /// <summary>The blurred sphere as a premultiplied bitmap at 1/<see cref="Scale"/> of the size.</summary>
    private static class HaloImage
    {
        public const int Scale = 4;

        public static WriteableBitmap Create(double diameter, double sigma, Size layer, Palette palette)
        {
            var width = (int)Math.Ceiling(layer.Width / Scale);
            var height = (int)Math.Ceiling(layer.Height / Scale);
            var length = width * height * 4;
            var plane = ArrayPool<float>.Shared.Rent(length);
            var temp = ArrayPool<float>.Shared.Rent(length);
            try
            {
                Array.Clear(plane, 0, length);
                return Render(diameter, sigma, layer, palette, width, height, plane, temp);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(plane);
                ArrayPool<float>.Shared.Return(temp);
            }
        }

        private static WriteableBitmap Render(double diameter, double sigma, Size layer, Palette palette, int width, int height, float[] plane, float[] temp)
        {

            // 1. The sphere: the CSS gradients over the lavender base, premultiplied, with a one-pixel soft rim.
            var blobs = palette.Blobs();
            var lavender = palette.Base;
            var radius = diameter / 2;
            var left = (layer.Width - diameter) / 2;
            var top = (layer.Height - diameter) / 2;
            var reach = new double[blobs.Length];
            for (var b = 0; b < blobs.Length; b++)
            {
                var dx = Math.Max(blobs[b].X, 1 - blobs[b].X);
                var dy = Math.Max(blobs[b].Y, 1 - blobs[b].Y);
                reach[b] = Math.Sqrt((dx * dx) + (dy * dy)) * diameter * blobs[b].Reach;
            }

            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var x = ((i + 0.5) * Scale) - left;
                    var y = ((j + 0.5) * Scale) - top;
                    var coverage = Math.Clamp(((radius - Math.Sqrt(((x - radius) * (x - radius)) + ((y - radius) * (y - radius)))) / Scale) + 0.5, 0, 1);
                    if (coverage <= 0)
                    {
                        continue;
                    }

                    double r = lavender.R, g = lavender.G, bl = lavender.B;
                    for (var b = 0; b < blobs.Length; b++)
                    {
                        var ddx = x - (blobs[b].X * diameter);
                        var ddy = y - (blobs[b].Y * diameter);
                        var a = Math.Max(0, 1 - (Math.Sqrt((ddx * ddx) + (ddy * ddy)) / reach[b]));
                        r += (blobs[b].Colour.R - r) * a;
                        g += (blobs[b].Colour.G - g) * a;
                        bl += (blobs[b].Colour.B - bl) * a;
                    }

                    var o = ((j * width) + i) * 4;
                    plane[o] = (float)(r / 255 * coverage);
                    plane[o + 1] = (float)(g / 255 * coverage);
                    plane[o + 2] = (float)(bl / 255 * coverage);
                    plane[o + 3] = (float)coverage;
                }
            }

            // 2. The Gaussian, separable, the outside is transparent.
            var sigmaPixels = sigma / Scale;
            var half = (int)Math.Ceiling(3 * sigmaPixels);
            var kernel = new float[(2 * half) + 1];
            var sum = 0f;
            for (var k = -half; k <= half; k++)
            {
                kernel[k + half] = (float)Math.Exp(-(k * k) / (2 * sigmaPixels * sigmaPixels));
                sum += kernel[k + half];
            }

            for (var k = 0; k < kernel.Length; k++)
            {
                kernel[k] /= sum;
            }

            Convolve(plane, temp, width, height, kernel, half, horizontal: true);
            Convolve(temp, plane, width, height, kernel, half, horizontal: false);

            // 3. Premultiplied BGRA.
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = bitmap.Lock())
            {
                // One row at a time through a pooled buffer, straight into the bitmap's memory.
                var row = ArrayPool<byte>.Shared.Rent(width * 4);
                try
                {
                    for (var j = 0; j < height; j++)
                    {
                        for (var i = 0; i < width; i++)
                        {
                            var o = ((j * width) + i) * 4;
                            row[i * 4] = ToByte(plane[o + 2]);
                            row[(i * 4) + 1] = ToByte(plane[o + 1]);
                            row[(i * 4) + 2] = ToByte(plane[o]);
                            row[(i * 4) + 3] = ToByte(plane[o + 3]);
                        }

                        System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + (j * frame.RowBytes), width * 4);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(row);
                }
            }

            return bitmap;
        }

        private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

        private static void Convolve(float[] source, float[] target, int width, int height, float[] kernel, int half, bool horizontal)
        {
            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    float c0 = 0, c1 = 0, c2 = 0, c3 = 0;
                    for (var k = -half; k <= half; k++)
                    {
                        var x = horizontal ? i + k : i;
                        var y = horizontal ? j : j + k;
                        if (x < 0 || y < 0 || x >= width || y >= height)
                        {
                            continue;
                        }

                        var w = kernel[k + half];
                        var o = ((y * width) + x) * 4;
                        c0 += source[o] * w;
                        c1 += source[o + 1] * w;
                        c2 += source[o + 2] * w;
                        c3 += source[o + 3] * w;
                    }

                    var t = ((j * width) + i) * 4;
                    target[t] = c0;
                    target[t + 1] = c1;
                    target[t + 2] = c2;
                    target[t + 3] = c3;
                }
            }
        }
    }

    /// <summary>The painted layer: the sphere (with its inner glow) or the halo (the same sphere, blurred).</summary>
    private sealed class Layer : Control
    {
        private readonly GlassOrbControl _owner;
        private readonly bool _isHalo;
        private IBrush[]? _blobs;
        private IBrush? _base;
        private IBrush? _glow;
        private Size _brushesFor;
        private WriteableBitmap? _haloBitmap;
        private (double Diameter, double Sigma, Size Layer, Palette Palette) _haloKey;

        public Layer(GlassOrbControl owner, bool isHalo)
        {
            _owner = owner;
            _isHalo = isHalo;
        }

        public bool HasBitmap => _haloBitmap is not null;

        /// <summary>The sphere's brushes are rebuilt on the next paint. The halo bitmap is not touched: its cache key decides.</summary>
        public void InvalidateLook()
        {
            _blobs = null;
            _glow = null;
            _brushesFor = default;
            IsVisible = !_isHalo || _owner.HaloBlur > 0;
            InvalidateVisual();
        }

        /// <summary>Frees the halo bitmap and forgets its key (the control left the visual tree).</summary>
        public void ReleaseHalo()
        {
            _haloBitmap?.Dispose();
            _haloBitmap = null;
            _haloKey = default;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (!_owner._hasColours || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }

            var side = Math.Min(_owner._size.Width, _owner._size.Height);
            if (_isHalo)
            {
                RenderHalo(context, side);
                return;
            }

            var diameter = side * _owner.BodyFraction;
            var rect = new Rect((Bounds.Width - diameter) / 2, (Bounds.Height - diameter) / 2, diameter, diameter);
            EnsureBrushes(diameter);

            context.DrawEllipse(_base, null, rect);
            foreach (var blob in _blobs!)
            {
                context.DrawEllipse(blob, null, rect);
            }

            if (_glow is not null)
            {
                context.DrawEllipse(_glow, null, rect);
            }
        }

        /// <summary>
        /// The halo is the sphere blurred with a Gaussian of the CSS <c>blur()</c>'s standard deviation, drawn with the group opacity.
        /// It is computed once on the CPU at a fraction of the size (a blurred image has no detail to lose) and cached, because the
        /// blur effect of the renderer does not reproduce the canvas (its radius is not the standard deviation and the layer is
        /// composited differently), and the halo is the one part of the orb that must match the reference.
        /// </summary>
        private void RenderHalo(DrawingContext context, double diameter)
        {
            var sigma = _owner.HaloBlur;
            if (sigma <= 0 || diameter <= 0)
            {
                return;
            }

            var key = (diameter, sigma, Bounds.Size, _owner._palette);
            if (_haloBitmap is null || _haloKey != key)
            {
                _haloBitmap?.Dispose();
                _owner.HaloBuilds++;
                _haloBitmap = HaloImage.Create(diameter, sigma, Bounds.Size, _owner._palette);
                _haloKey = key;
            }

            using (context.PushOpacity(_owner.HaloOpacity))
            {
                var scale = HaloImage.Scale;
                var target = new Rect(0, 0, _haloBitmap.PixelSize.Width * scale, _haloBitmap.PixelSize.Height * scale);
                context.DrawImage(_haloBitmap, new Rect(_haloBitmap.Size), target);
            }
        }

        private void EnsureBrushes(double diameter)
        {
            var key = new Size(diameter, 0);
            if (_blobs is not null && _brushesFor == key)
            {
                return;
            }

            _brushesFor = key;
            _base = new ImmutableSolidColorBrush(_owner._palette.Base);

            var blobs = _owner._palette.Blobs();
            var brushes = new IBrush[blobs.Length];
            for (var i = 0; i < blobs.Length; i++)
            {
                brushes[i] = BlobBrush(blobs[i]);
            }

            _blobs = brushes;
            _glow = !_isHalo && _owner.InnerGlowBlur > 0 && _owner.InnerGlowOpacity > 0 ? GlowBrush(diameter) : null;
        }

        /// <summary>
        /// A CSS <c>radial-gradient(circle at X Y, colour 0%, transparent Reach)</c>: the radius is Reach of the distance from
        /// the centre to the farthest corner of the (square) element.
        /// </summary>
        private static IBrush BlobBrush(Blob blob)
        {
            var farthest = Math.Sqrt((Math.Max(blob.X, 1 - blob.X) * Math.Max(blob.X, 1 - blob.X))
                + (Math.Max(blob.Y, 1 - blob.Y) * Math.Max(blob.Y, 1 - blob.Y)));
            var radius = farthest * blob.Reach;
            var clear = Color.FromArgb(0, blob.Colour.R, blob.Colour.G, blob.Colour.B);
            return new RadialGradientBrush
            {
                Center = new RelativePoint(blob.X, blob.Y, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(blob.X, blob.Y, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(radius, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(blob.Colour, 0),
                    new GradientStop(clear, 1),
                },
            }.ToImmutable();
        }

        /// <summary>
        /// The inner glow, <c>box-shadow: inset 0 0 B px</c>: a white ring whose opacity falls off like the tail of a Gaussian
        /// with sigma B/2 (half of the full strength right at the rim).
        /// </summary>
        private IBrush GlowBrush(double diameter)
        {
            var radius = diameter / 2;
            var sigma = _owner.InnerGlowBlur / 2;
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            };

            // Depth below the rim in multiples of sigma, from the outside in; the last stop is fully clear.
            double[] depths = [0, 0.5, 1, 1.5, 2, 2.5, 3];
            var stops = new List<GradientStop>(depths.Length + 1);
            for (var i = depths.Length - 1; i >= 0; i--)
            {
                var offset = Math.Max(0, 1 - (depths[i] * sigma / radius));
                var alpha = _owner.InnerGlowOpacity * (1 - NormalCdf(depths[i]));
                stops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * alpha), 255, 255, 255), offset));
            }

            foreach (var stop in stops)
            {
                brush.GradientStops.Add(stop);
            }

            return brush.ToImmutable();
        }

        /// <summary>Standard normal CDF (Abramowitz-Stegun 7.1.26 on erf).</summary>
        private static double NormalCdf(double x)
        {
            var z = Math.Abs(x) / Math.Sqrt(2);
            var t = 1 / (1 + (0.3275911 * z));
            var poly = ((((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t) - 0.284496736) * t) + 0.254829592) * t;
            var erf = 1 - (poly * Math.Exp(-z * z));
            return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
        }
    }
}
