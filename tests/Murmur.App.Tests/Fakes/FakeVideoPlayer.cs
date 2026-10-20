using Murmur.Core.Capture;

namespace Murmur.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IVideoPlayer"/>: records every call, the test sets position and playing state, and the pictures
/// are a solid colour. A picture is "new" once per <see cref="NewPicture"/> call, or on every call with
/// <see cref="Continuous"/>.
/// </summary>
internal sealed class FakeVideoPlayer : IVideoPlayer
{
    private readonly TimeSpan _fileDuration;
    private bool _fresh;

    public FakeVideoPlayer(TimeSpan fileDuration, VideoSize? size = null)
    {
        _fileDuration = fileDuration;
        FileSize = size ?? new VideoSize(1920, 1080);
    }

    public VideoSize FileSize { get; set; }

    public bool IsOpen { get; private set; }

    public TimeSpan Duration { get; private set; }

    public VideoSize Size { get; private set; }

    public TimeSpan Position { get; set; }

    public bool IsPlaying { get; set; }

    public List<string> Opens { get; } = [];

    public List<TimeSpan> Seeks { get; } = [];

    public List<string> Calls { get; } = [];

    public int Closes { get; private set; }

    public int Plays { get; private set; }

    public int Pauses { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Completes the open when set by the test; null = the open completes at once.</summary>
    public TaskCompletionSource? OpenGate { get; set; }

    /// <summary>The open ignores cancellation, as a player may that is already past its last cancellation point.</summary>
    public bool IgnoreCancel { get; set; }

    public Exception? OpenFailure { get; set; }

    public bool ThrowOnClose { get; set; }

    public bool ThrowOnCopy { get; set; }

    /// <summary>Colour of the pictures, 0xAARRGGBB.</summary>
    public uint Color { get; set; } = 0xFF2040C0;

    /// <summary>Every <see cref="TryCopyFrame"/> delivers a picture.</summary>
    public bool Continuous { get; set; }

    /// <summary>Seek throws, as a decoder that died would.</summary>
    public bool ThrowOnSeek { get; set; }

    public int CopyCalls { get; private set; }

    public int Delivered { get; private set; }

    public int LastWidth { get; private set; }

    public int LastHeight { get; private set; }

    public int LastStride { get; private set; }

    public event Action<string>? Failed;

    public async ValueTask OpenAsync(string filePath, CancellationToken cancellationToken)
    {
        Opens.Add(filePath);
        Calls.Add("open");
        if (OpenGate is { } gate)
        {
            // The gate holds up this open only; the next one is not gated.
            OpenGate = null;
            await (IgnoreCancel ? gate.Task : gate.Task.WaitAsync(cancellationToken)).ConfigureAwait(true);
        }

        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        IsOpen = true;
        Duration = _fileDuration;
        Size = FileSize;
        Position = TimeSpan.Zero;
        IsPlaying = false;
        _fresh = true;
    }

    public void Close()
    {
        Closes++;
        Calls.Add("close");
        if (ThrowOnClose)
        {
            throw new InvalidOperationException("close failed");
        }

        IsOpen = false;
        IsPlaying = false;
        Duration = TimeSpan.Zero;
        Size = default;
        Position = TimeSpan.Zero;
    }

    public void Play()
    {
        Plays++;
        Calls.Add("play");
        IsPlaying = true;
    }

    public void Pause()
    {
        Pauses++;
        Calls.Add("pause");
        IsPlaying = false;
    }

    public void Seek(TimeSpan position)
    {
        if (ThrowOnSeek)
        {
            throw new InvalidOperationException("decoder lost");
        }

        Seeks.Add(position);
        Calls.Add("seek");
        Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
        _fresh = true;
    }

    /// <summary>The next <see cref="TryCopyFrame"/> delivers a picture.</summary>
    public void NewPicture() => _fresh = true;

    public bool TryCopyFrame(Span<byte> destination, int width, int height, int stride)
    {
        CopyCalls++;
        if (ThrowOnCopy)
        {
            throw new InvalidOperationException("decoder lost");
        }

        if (!IsOpen || !(_fresh || Continuous))
        {
            return false;
        }

        _fresh = false;
        Delivered++;
        LastWidth = width;
        LastHeight = height;
        LastStride = stride;
        for (var y = 0; y < height; y++)
        {
            var row = destination.Slice(y * stride, width * 4);
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row).Fill(Color);
        }

        return true;
    }

    public void RaiseFailed(string message) => Failed?.Invoke(message);

    public void Dispose() => Disposed = true;
}

/// <summary>Hands out <see cref="FakeVideoPlayer"/>s (one per call).</summary>
internal sealed class FakeVideoPlayback(Func<FakeVideoPlayer> create, bool supported = true) : IVideoPlayback
{
    public FakeVideoPlayback(FakeVideoPlayer player, bool supported = true)
        : this(() => player, supported)
    {
    }

    public bool IsSupported { get; } = supported;

    public List<FakeVideoPlayer> Players { get; } = [];

    public int Created => Players.Count;

    public IVideoPlayer CreateVideoPlayer()
    {
        var player = create();
        Players.Add(player);
        return player;
    }
}
