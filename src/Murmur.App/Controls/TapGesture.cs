using Avalonia;

namespace Murmur.App.Controls;

/// <summary>
/// The press, move, release and cancel of one touch or pen contact, reduced to "was it a tap, and on which word". A contact
/// that stays within <c>slop</c> of where it went down and is released without being cancelled (the scroll took it over, the
/// pointer capture moved, a second finger came down) is a tap; anything else is not. Holds no references and allocates nothing.
/// </summary>
internal struct TapGesture
{
    private int _pointerId;
    private Point _origin;
    private int _word;
    private bool _active;

    /// <summary>A contact is down and has not been cancelled, moved too far or released.</summary>
    public readonly bool IsActive => _active;

    /// <summary>The word under the contact when it went down.</summary>
    public readonly int Word => _word;

    /// <summary>
    /// A contact went down. A second contact while one is down (pinch, two fingers) cancels the gesture, and the new contact
    /// is not tracked either.
    /// </summary>
    public void Press(int pointerId, Point position, int word)
    {
        if (_active)
        {
            Cancel();
            return;
        }

        _pointerId = pointerId;
        _origin = position;
        _word = word;
        _active = true;
    }

    /// <summary>The contact moved; going further than <paramref name="slop"/> (DIPs) from the press turns it into a drag, so it is no tap.</summary>
    public void Move(int pointerId, Point position, double slop)
    {
        if (!_active || pointerId != _pointerId)
        {
            return;
        }

        var dx = position.X - _origin.X;
        var dy = position.Y - _origin.Y;
        if ((dx * dx) + (dy * dy) > slop * slop)
        {
            Cancel();
        }
    }

    /// <summary>The scroll took the gesture over, the pointer capture was lost or the contact was interrupted: no tap.</summary>
    public void Cancel()
    {
        _active = false;
        _word = -1;
    }

    /// <summary>
    /// The contact was released at <paramref name="position"/>. Returns the word to act on when it was a tap (the release
    /// itself is checked against <paramref name="slop"/> too), otherwise -1. The gesture ends either way.
    /// </summary>
    public int Release(int pointerId, Point position, double slop)
    {
        if (!_active || pointerId != _pointerId)
        {
            return -1;
        }

        Move(pointerId, position, slop);
        var word = _active ? _word : -1;
        Cancel();
        return word;
    }
}
