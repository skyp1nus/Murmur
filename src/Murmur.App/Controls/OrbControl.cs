using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The recording orb: a soft Violet-to-Ember radial blob with a glow that "breathes" with the audio level.
/// This control is (together with icons) the only place the accent colours may be used. The colours come from the
/// shared token resources (<c>Color.AccentViolet</c>, <c>Color.AccentEmber</c>), never from literals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drawing happens once.</b> The orb is two ellipses (glow and body) with gradient brushes that are built once when the
/// colours are resolved. Breathing never redraws them: every update only sets the <c>Scale</c> and <c>Opacity</c> of the
/// two ellipses' composition visuals, so the UI thread records no drawing commands and allocates nothing per frame.
/// </para>
/// <para>
/// <b>Animation.</b> While <see cref="IsActive"/> is false the orb shows one static frame and requests no animation
/// frames, so an idle window costs no CPU. While active it runs a 15 Hz timer (not the 60 Hz animation-frame loop, whose ticks
/// alone cost CPU), and reads <see cref="LevelSource"/> once per update. While <see cref="Motion.ReduceMotionProperty"/> is on
/// the breathing is decoration the user asked to do without: the orb shows its static frame and the timer is stopped.
/// </para>
/// <para>
/// <b>Time.</b> Every update reads a single clock (<see cref="Clock"/>, the system clock unless a test replaces it). Each
/// (re)start of the animation (activation, re-attachment, a new clock) begins at the current level with the breath at
/// rest, so a frame depends only on the clock readings since the last start, never on when an earlier timer tick
/// happened to run.
/// </para>
/// </remarks>
public sealed class OrbControl : Panel
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<OrbControl, bool>(nameof(IsActive));

    public static readonly StyledProperty<ILevelSource?> LevelSourceProperty =
        AvaloniaProperty.Register<OrbControl, ILevelSource?>(nameof(LevelSource));

    // Resting body size as a fraction of the control; the glow is GlowFactor times bigger and fades out at its rim.
    private const double BodyFraction = 0.56;
    private const double GlowFactor = 1.75;
    private const double MaxLevelScale = 0.22;
    private const double BreathAmplitude = 0.018;
    private const double BreathPeriodSeconds = 4.0;
    private const double AttackPerSecond = 14.0;
    private const double ReleasePerSecond = 3.5;
    private const double MaxStepSeconds = 0.1;
    private const double MinUpdateSeconds = 0.066;
    private const double LevelSkipThreshold = 0.02;
    private const double BreathSkipThreshold = 0.003;
    private const double QuietLevel = 0.1;
    private const double QuietBreathShare = 0.4;
    private const float IdleGlowOpacity = 0.5f;

    private readonly Ellipse _glow = new() { IsHitTestVisible = false };
    private readonly Ellipse _body = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _timer;
    private TimeProvider _clock = TimeProvider.System;
    private long _clockStart;
    private bool _primed;
    private TimeSpan _lastFrame;
    private double _smoothed;
    private double _phaseSeconds;

    // The frame on screen; the idle frame until the first update of a recording.
    private double _appliedLevel;
    private double _appliedBreath;
    private bool _appliedActive;
    private long _appliedFrames;
    private Size _size;

    public OrbControl()
    {
        _clockStart = _clock.GetTimestamp();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(MinUpdateSeconds) };
        _timer.Tick += OnTick;
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_glow);
        Children.Add(_body);
        ActualThemeVariantChanged += (_, _) => BuildBrushes();
    }

    /// <summary>True while recording: turns the animation loop on.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Where the orb reads the audio level from, once per update.</summary>
    public ILevelSource? LevelSource
    {
        get => GetValue(LevelSourceProperty);
        set => SetValue(LevelSourceProperty, value);
    }

    /// <summary>The brush of the orb body, rebuilt whenever the theme variant changes (for tests).</summary>
    internal IBrush? BodyFill => _body.Fill;

    /// <summary>Smoothed level in [0, 1] of the last update (for tests).</summary>
    internal double SmoothedLevel => _smoothed;

    /// <summary>True while the control is running its update timer (for tests).</summary>
    internal bool IsFrameLoopRunning => _timer.IsEnabled;

    /// <summary>How many frames were pushed to the composition visuals; unchanged frames are skipped (for tests).</summary>
    internal long AppliedFrames => _appliedFrames;

    /// <summary>Current composition scale of the body (for tests); 0 when not attached.</summary>
    internal double BodyScale => ElementComposition.GetElementVisual(_body)?.Scale.X ?? 0;

    /// <summary>Current composition opacity of the glow (for tests); 0 when not attached.</summary>
    internal double GlowOpacity => ElementComposition.GetElementVisual(_glow)?.Opacity ?? 0;

    /// <summary>
    /// The clock every update reads; the system clock by default. Replacing it restarts the animation, so nothing that
    /// ran before (such as a timer tick at some real moment) affects later frames. Internal: tests set a manual clock to
    /// render the same frame on every run.
    /// </summary>
    internal TimeProvider Clock
    {
        get => _clock;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _clock = value;
            _clockStart = value.GetTimestamp();
            Restart();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);
        var size = new Size(side, side);
        _glow.Measure(size);
        _body.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _size = finalSize;
        var rect = new Rect(finalSize);
        _glow.Arrange(rect);
        _body.Arrange(rect);

        // The centre may have moved: re-centre the frame on screen without changing it, so a layout pass during a
        // recording never makes the orb jump.
        PushFrame();
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BuildBrushes();
        PushFrame();
        UpdateLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Restart();
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            Restart();
            if (!IsActive)
            {
                HoldRest();
            }

            UpdateLoop();
        }
        else if (change.Property == Motion.ReduceMotionProperty)
        {
            Restart();
            if (ReduceMotion)
            {
                HoldRest();
            }

            UpdateLoop();
        }
    }

    /// <summary>True while the system asks for less motion (inherited from the window): the orb holds its static frame.</summary>
    private bool ReduceMotion => Motion.GetReduceMotion(this);

    /// <summary>Shows the static frame and forgets the running animation.</summary>
    private void HoldRest()
    {
        _smoothed = 0;
        ApplyFrame(0, breath: 0, active: false);
    }

    /// <summary>Forgets the running animation: the next update starts again from the current level with the breath at rest.</summary>
    private void Restart() => _primed = false;

    private void UpdateLoop()
    {
        var run = IsActive && !ReduceMotion && ((Visual)this).IsAttachedToVisualTree();
        if (run && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!run && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e) => Tick();

    /// <summary>One animation step at the clock's current time: exactly what the timer does (internal for tests).</summary>
    internal void Tick() => Update(_clock.GetElapsedTime(_clockStart));

    /// <summary>
    /// One animation step at <paramref name="timestamp"/> on the orb's clock; internal so tests can drive time themselves.
    /// A step less than one update interval after the previous one changes nothing.
    /// </summary>
    internal void Update(TimeSpan timestamp)
    {
        if (ReduceMotion)
        {
            return;
        }

        var elapsed = (timestamp - _lastFrame).TotalSeconds;
        if (_primed && elapsed < MinUpdateSeconds)
        {
            return;
        }

        // Perceptual curve: quiet speech should still move the orb visibly. NaN or negative levels count as silence.
        var level = LevelSource?.Level ?? 0f;
        var target = level > 0f ? Math.Sqrt(Math.Min(level, 1f)) : 0;

        var starting = !_primed;
        if (starting)
        {
            // A (re)start begins at the current level with the breath at rest, whatever happened before it.
            _primed = true;
            _smoothed = target;
            _phaseSeconds = 0;
        }
        else
        {
            var dt = Math.Min(elapsed, MaxStepSeconds);
            var rate = target > _smoothed ? AttackPerSecond : ReleasePerSecond;
            _smoothed += (target - _smoothed) * (1 - Math.Exp(-rate * dt));
            _phaseSeconds += dt;
        }

        _lastFrame = timestamp;

        // The breathing fades towards silence, so a quiet room changes (and repaints) the orb far less often.
        var breathShare = QuietBreathShare + ((1 - QuietBreathShare) * Math.Min(1, _smoothed / QuietLevel));
        var breath = Math.Sin(_phaseSeconds * (2 * Math.PI / BreathPeriodSeconds)) * BreathAmplitude * breathShare;

        // Every change of a composition visual makes the compositor re-rasterise the (software) gradient glow,
        // so changes too small to see are not pushed at all. The first frame of a start is always pushed.
        if (starting || Math.Abs(_smoothed - _appliedLevel) >= LevelSkipThreshold ||
            Math.Abs(breath - _appliedBreath) >= BreathSkipThreshold)
        {
            ApplyFrame(_smoothed, breath, active: true);
        }
    }

    /// <summary>Puts a new frame on screen.</summary>
    private void ApplyFrame(double level, double breath, bool active)
    {
        _appliedLevel = level;
        _appliedBreath = breath;
        _appliedActive = active;
        PushFrame();
    }

    /// <summary>
    /// Pushes the frame on screen to the composition visuals: scales the two layers about the centre and fades the glow
    /// (no redraw, no allocation). Layout calls it on its own to re-centre the same frame.
    /// </summary>
    private void PushFrame()
    {
        _appliedFrames++;
        var bodyScale = BodyFraction * (1 + (_appliedLevel * MaxLevelScale) + _appliedBreath);
        var glowScale = bodyScale * GlowFactor;
        var glowOpacity = _appliedActive ? (float)(0.45 + (0.55 * _appliedLevel)) : IdleGlowOpacity;
        var centre = new Vector3D(_size.Width / 2, _size.Height / 2, 0);

        if (ElementComposition.GetElementVisual(_body) is { } body)
        {
            body.CenterPoint = centre;
            body.Scale = new Vector3D(bodyScale, bodyScale, 1);
        }

        if (ElementComposition.GetElementVisual(_glow) is { } glow)
        {
            glow.CenterPoint = centre;
            glow.Scale = new Vector3D(glowScale, glowScale, 1);
            glow.Opacity = glowOpacity;
        }
    }

    private void BuildBrushes()
    {
        if (!this.TryFindResource("Color.AccentViolet", ActualThemeVariant, out var violetValue) || violetValue is not Color violet ||
            !this.TryFindResource("Color.AccentEmber", ActualThemeVariant, out var emberValue) || emberValue is not Color ember)
        {
            _body.Fill = null;
            _glow.Fill = null;
            return;
        }

        // Body: violet core drifting to ember at the rim, feathered to transparent for a soft edge.
        _body.Fill = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
            RadiusX = RelativeScalar.Parse("50%"),
            RadiusY = RelativeScalar.Parse("50%"),
            GradientStops =
            {
                new GradientStop(violet, 0.0),
                new GradientStop(Mix(violet, ember, 0.5), 0.55),
                new GradientStop(ember, 0.86),
                new GradientStop(Color.FromArgb(0, ember.R, ember.G, ember.B), 1.0),
            },
        }.ToImmutable();

        _glow.Fill = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = RelativeScalar.Parse("50%"),
            RadiusY = RelativeScalar.Parse("50%"),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(120, violet.R, violet.G, violet.B), 0.0),
                new GradientStop(Color.FromArgb(90, ember.R, ember.G, ember.B), 0.5),
                new GradientStop(Color.FromArgb(0, ember.R, ember.G, ember.B), 1.0),
            },
        }.ToImmutable();
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));
}
