using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>One screen's work area (the screen without the taskbar), in physical pixels, with its scaling (1.0 = 100 %).</summary>
public readonly record struct WorkArea(int X, int Y, int Width, int Height, double Scaling)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    internal bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>Where the orb sits: the top-left of its 44 DIP circle in physical pixels, and whether a saved position had to be moved.</summary>
public readonly record struct OrbPlacement(int X, int Y, bool Corrected);

/// <summary>
/// Where the floating orb goes (#46). Pure maths over the screens' work areas, so a monitor that was unplugged is covered by a test
/// with a fake list of screens.
/// </summary>
/// <remarks>
/// The saved <see cref="FloatingOrbPosition"/> is in DIPs of the primary screen (pixels divided by its scaling): one linear scale over
/// the whole virtual desktop, so a spot is unambiguous on any number of screens whatever their scaling. The orb itself is 44 DIPs of
/// the screen it is on (44 px at 100 %, 66 px at 150 %). The first work area of the list is the primary one.
/// </remarks>
public static class FloatingOrbPlacement
{
    /// <summary>The orb's diameter in DIPs (the white circle).</summary>
    public const double OrbSize = 44;

    /// <summary>The distance of the default spot from the edges of the primary work area, in DIPs.</summary>
    public const double DefaultMargin = 24;

    /// <summary>
    /// The top-left of the orb in pixels: the saved spot when it is on a screen, the nearest spot with the whole orb inside a work area when it is not, the
    /// bottom-right corner of the primary work area when nothing was saved. <c>null</c> without any screen.
    /// </summary>
    public static OrbPlacement? Resolve(FloatingOrbPosition? saved, IReadOnlyList<WorkArea> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        if (areas.Count == 0)
        {
            return null;
        }

        if (saved is null || !double.IsFinite(saved.X) || !double.IsFinite(saved.Y))
        {
            return DefaultSpot(areas[0]);
        }

        var scale = areas[0].Scaling;
        var spotX = saved.X * scale;
        var spotY = saved.Y * scale;

        // The work area the orb's centre is in, or else the one nearest to its centre (a monitor was unplugged): the whole orb is then
        // put inside it, so an orb dragged half off an edge, or left under the taskbar, comes back whole.
        var best = areas[0];
        var bestDistance = double.MaxValue;
        foreach (var area in areas)
        {
            var half = OrbSize * area.Scaling / 2;
            var distance = DistanceTo(area, spotX + half, spotY + half);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = area;
            }
        }

        var size = OrbSize * best.Scaling;
        var x = (int)Math.Round(Math.Clamp(spotX, best.X, Math.Max(best.X, best.Right - size)));
        var y = (int)Math.Round(Math.Clamp(spotY, best.Y, Math.Max(best.Y, best.Bottom - size)));
        return new OrbPlacement(x, y, Corrected: x != (int)Math.Round(spotX) || y != (int)Math.Round(spotY));
    }

    /// <summary>The position to save for an orb whose top-left is at (<paramref name="x"/>, <paramref name="y"/>) pixels: DIPs of the primary screen.</summary>
    public static FloatingOrbPosition? ToSaved(int x, int y, IReadOnlyList<WorkArea> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        if (areas.Count == 0)
        {
            return null;
        }

        var scale = areas[0].Scaling;
        return new FloatingOrbPosition(x / scale, y / scale);
    }

    /// <summary>
    /// Whether the pill opens to the right of the circle instead of to its left (the mockup): when the circle at (<paramref name="x"/>,
    /// <paramref name="y"/>) pixels is closer to the left edge of its work area than the pill's side of the window
    /// (<paramref name="pillSideDips"/>, in DIPs of that screen). Then the window, which keeps the pill's room on that side, would reach
    /// into the screen on the left: on mixed scaling Windows gives a window the scaling of the monitor that holds most of it, and the
    /// orb would take the other monitor's scaling (and could flip between both); on a single screen the pill would open off it.
    /// </summary>
    public static bool PillOnRight(int x, int y, IReadOnlyList<WorkArea> areas, double pillSideDips)
    {
        ArgumentNullException.ThrowIfNull(areas);
        if (areas.Count == 0)
        {
            return false;
        }

        var area = AreaOf(x, y, areas);
        return x - area.X < pillSideDips * area.Scaling;
    }

    /// <summary>The work area the orb whose top-left is at (<paramref name="x"/>, <paramref name="y"/>) pixels is on: by its centre, else the nearest.</summary>
    internal static WorkArea AreaOf(int x, int y, IReadOnlyList<WorkArea> areas)
    {
        var best = areas[0];
        var bestDistance = double.MaxValue;
        foreach (var area in areas)
        {
            var half = OrbSize * area.Scaling / 2;
            var distance = DistanceTo(area, x + half, y + half);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = area;
            }
        }

        return best;
    }

    private static OrbPlacement DefaultSpot(WorkArea area)
    {
        var size = OrbSize * area.Scaling;
        var margin = DefaultMargin * area.Scaling;
        return new OrbPlacement((int)Math.Round(area.Right - margin - size), (int)Math.Round(area.Bottom - margin - size), Corrected: false);
    }

    /// <summary>The squared distance from the point to the work area (0 = the point is inside it).</summary>
    private static double DistanceTo(WorkArea area, double x, double y)
    {
        var dx = x < area.X ? area.X - x : x >= area.Right ? x - area.Right + 1 : 0;
        var dy = y < area.Y ? area.Y - y : y >= area.Bottom ? y - area.Bottom + 1 : 0;
        return (dx * dx) + (dy * dy);
    }
}
