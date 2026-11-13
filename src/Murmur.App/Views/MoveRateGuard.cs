namespace Murmur.App.Views;

/// <summary>
/// Lets a window move itself at most <c>max</c> times within a sliding <c>window</c> of time: the floating orb moves itself after
/// layout and after a change of scaling, and on mixed-DPI screens a move can change the window's scaling again. This breaks such a
/// loop (each move would otherwise trigger the next) without getting in the way of the few moves a user's action causes.
/// </summary>
/// <remarks>Pure and allocation-free after construction; the times are milliseconds of any monotonic clock.</remarks>
internal sealed class MoveRateGuard
{
    private readonly long[] _times;
    private readonly long _window;
    private int _next;
    private int _count;

    public MoveRateGuard(int max, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        _times = new long[max];
        _window = (long)window.TotalMilliseconds;
    }

    /// <summary>Whether a move may happen at <paramref name="nowMs"/>; a move that may is counted.</summary>
    public bool TryEnter(long nowMs)
    {
        // The oldest of the last max moves: when it is still inside the window, this move would be one too many.
        if (_count == _times.Length && nowMs - _times[_next] < _window)
        {
            return false;
        }

        _times[_next] = nowMs;
        _next = (_next + 1) % _times.Length;
        _count = Math.Min(_count + 1, _times.Length);
        return true;
    }
}
