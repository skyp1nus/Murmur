using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace Murmur.App.Controls;

/// <summary>
/// The samples of a strip of level bars on their way from the UI thread to the render thread: a fixed ring with one writer (the UI
/// thread) and one reader (the render thread), so a sample is handed over without a lock and without an allocation.
/// </summary>
internal sealed class LevelBarsFeed
{
    /// <summary>Samples the ring holds; the render thread reads them many times faster than they come.</summary>
    public const int Capacity = 64;

    private readonly float[] _values = new float[Capacity];
    private readonly bool[] _animate = new bool[Capacity];
    private long _written;

    /// <summary>How many samples were written so far.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>Writes a sample (UI thread).</summary>
    public void Write(float value, bool animate)
    {
        var index = _written;
        _values[index & (Capacity - 1)] = value;
        _animate[index & (Capacity - 1)] = animate;
        Volatile.Write(ref _written, index + 1);
    }

    /// <summary>Reads the next sample after <paramref name="cursor"/> (render thread); false when there is none.</summary>
    public bool TryRead(ref long cursor, out float value, out bool animate)
    {
        var written = Written;
        if (written - cursor > Capacity)
        {
            cursor = written - Capacity;
        }

        if (cursor >= written)
        {
            value = 0;
            animate = false;
            return false;
        }

        value = _values[cursor & (Capacity - 1)];
        animate = _animate[cursor & (Capacity - 1)];
        cursor++;
        return true;
    }
}

/// <summary>The brushes and the state of a strip (a message to the render thread; immutable).</summary>
internal sealed class LevelBarsLook(IImmutableBrush? first, IImmutableBrush? second, bool idle)
{
    /// <summary>Past bars (History) or live bars (Meter).</summary>
    public IImmutableBrush? First { get; } = first;

    /// <summary>Stubs (History) or idle bars (Meter).</summary>
    public IImmutableBrush? Second { get; } = second;

    /// <summary>Every bar is a stub (a History track that is not recording).</summary>
    public bool Idle { get; } = idle;
}

/// <summary>All the values at once, at rest (a message to the render thread; immutable): after a clear, a re-attach or a burst.</summary>
internal sealed class LevelBarsSnapshot(float[] values, long cursor)
{
    public float[] Values { get; } = values;

    /// <summary>The feed position the values already include: the render thread reads only the samples after it.</summary>
    public long Cursor { get; } = cursor;
}

/// <summary>
/// The render-thread side of a strip of level bars: it takes the samples from the <see cref="LevelBarsFeed"/>, slides the bars with
/// <see cref="LevelBarsMotion"/> and draws them with cached immutable brushes into a preallocated array of bars. It asks for the next
/// animation frame only while the strip slides: a strip at rest costs the render loop nothing.
/// </summary>
internal sealed class LevelBarsHandler : CompositionCustomVisualHandler
{
    /// <summary>The message that says "there are new samples in the feed" (one object for every sample, so nothing is allocated).</summary>
    public static readonly object Wake = new();

    private readonly LevelBarsMotion _motion;
    private readonly LevelBarsFeed _feed;
    private readonly LevelBar[] _bars;
    private LevelBarsLook? _look;
    private long _cursor;
    private bool _scheduled;

    public LevelBarsHandler(LevelBarsStyle style, LevelBarsFeed feed)
    {
        _motion = new LevelBarsMotion(style);
        _feed = feed;
        _bars = new LevelBar[style.MaxRects];
    }

    /// <summary>The handler has asked for the next animation frame: the strip is sliding (for tests).</summary>
    internal bool IsScheduled => Volatile.Read(ref _scheduled);

    /// <summary>The render thread's strip (for tests).</summary>
    internal LevelBarsMotion Motion => _motion;

    public override void OnMessage(object message)
    {
        var now = CompositionNow.TotalSeconds;
        if (message is LevelBarsLook look)
        {
            _look = look;
        }
        else if (message is LevelBarsSnapshot snapshot)
        {
            _motion.Reset(snapshot.Values, now);
            _cursor = snapshot.Cursor;
        }

        while (_feed.TryRead(ref _cursor, out var value, out var animate))
        {
            _motion.Push(value, now, animate);
        }

        Invalidate();
        Schedule(now);
    }

    public override void OnAnimationFrameUpdate()
    {
        _scheduled = false;
        Invalidate();
        Schedule(CompositionNow.TotalSeconds);
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_look is not { } look)
        {
            return;
        }

        var size = EffectiveSize;
        var count = _motion.Layout(_bars, size.X, size.Y, _motion.Position(CompositionNow.TotalSeconds), look.Idle);
        using (context.PushClip(new Rect(0, 0, size.X, size.Y)))
        {
            for (var i = 0; i < count; i++)
            {
                var bar = _bars[i];
                var brush = bar.Second ? look.Second : look.First;
                if (brush is null || bar.Opacity <= 0)
                {
                    continue;
                }

                var rect = new Rect(bar.X, bar.Y, bar.Width, bar.Height);
                if (bar.Opacity < 1)
                {
                    using (context.PushOpacity(bar.Opacity, rect))
                    {
                        context.DrawRectangle(brush, null, rect, bar.Radius, bar.Radius);
                    }
                }
                else
                {
                    context.DrawRectangle(brush, null, rect, bar.Radius, bar.Radius);
                }
            }
        }
    }

    private void Schedule(double now)
    {
        if (!_scheduled && _motion.IsMoving(now))
        {
            _scheduled = true;
            RegisterForNextAnimationFrameUpdate();
        }
    }
}

/// <summary>
/// The UI-thread side of a strip of level bars, owned by a control: it puts a <see cref="LevelBarsHandler"/> visual on the control
/// while it is attached, keeps it the control's size, and sends it the look, the samples and the snapshots. It also keeps the values at
/// rest itself, for the snapshot after a re-attach and for drawing a static frame when there is no compositor.
/// </summary>
internal sealed class LevelBarsHost
{
    private readonly Visual _owner;
    private readonly LevelBarsFeed _feed = new();
    private readonly LevelBarsMotion _rest;
    private CompositionCustomVisual? _visual;
    private LevelBarsHandler? _handler;
    private LevelBarsLook _look = new(null, null, idle: false);
    private IBrush? _first;
    private IBrush? _second;

    public LevelBarsHost(Visual owner, LevelBarsStyle style)
    {
        _owner = owner;
        _rest = new LevelBarsMotion(style);
    }

    /// <summary>The compositor draws the bars (the control draws nothing itself).</summary>
    public bool HasVisual => _visual is not null;

    /// <summary>The values at rest, oldest first (the first one is off the strip).</summary>
    public LevelBarsMotion Rest => _rest;

    /// <summary>The current look.</summary>
    public LevelBarsLook Look => _look;

    /// <summary>The render-thread side while attached (for tests; the headless renderer runs it on the test's thread).</summary>
    internal LevelBarsHandler? Handler => _handler;

    /// <summary>The samples sent to the render thread so far (for tests).</summary>
    public long SamplesSent => _feed.Written;

    /// <summary>Puts the bars' visual on the control (call it when the control is attached).</summary>
    public void Attach()
    {
        if (_visual is not null || ElementComposition.GetElementVisual(_owner) is not { } host)
        {
            return;
        }

        _handler = new LevelBarsHandler(_rest.Style, _feed);
        _visual = host.Compositor.CreateCustomVisual(_handler);
        _visual.Size = new Vector(_owner.Bounds.Width, _owner.Bounds.Height);
        ElementComposition.SetElementChildVisual(_owner, _visual);
        _visual.SendHandlerMessage(_look);
        SendSnapshot();
    }

    /// <summary>Takes the bars' visual off the control (call it when the control is detached).</summary>
    public void Detach()
    {
        if (_visual is null)
        {
            return;
        }

        ElementComposition.SetElementChildVisual(_owner, null);
        _visual = null;
        _handler = null;
    }

    /// <summary>The control was arranged to <paramref name="size"/>.</summary>
    public void Resize(Size size)
    {
        if (_visual is not null)
        {
            _visual.Size = new Vector(size.Width, size.Height);
        }
    }

    /// <summary>New brushes or state; sent only when something changed.</summary>
    public void SetLook(IBrush? first, IBrush? second, bool idle)
    {
        if (ReferenceEquals(first, _first) && ReferenceEquals(second, _second) && idle == _look.Idle)
        {
            return;
        }

        _first = first;
        _second = second;
        _look = new LevelBarsLook(first?.ToImmutable(), second?.ToImmutable(), idle);
        _visual?.SendHandlerMessage(_look);
    }

    /// <summary>A new sample (UI thread): it slides in on the compositor when <paramref name="animate"/>. Allocates nothing.</summary>
    public void Push(float value, bool animate)
    {
        _rest.Push(value, 0, animate: false);
        if (_visual is not null)
        {
            _feed.Write(value, animate);
            _visual.SendHandlerMessage(LevelBarsHandler.Wake);
        }
    }

    /// <summary>All the values at once (oldest first), at rest: a clear or a refill.</summary>
    public void Reset(ReadOnlySpan<float> values)
    {
        _rest.Reset(values, 0);
        SendSnapshot();
    }

    private void SendSnapshot()
    {
        if (_visual is null)
        {
            return;
        }

        // The rest values without the slot that has already left the strip.
        _visual.SendHandlerMessage(new LevelBarsSnapshot(_rest.Values[1..].ToArray(), _feed.Written));
    }
}
