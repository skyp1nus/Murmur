using Avalonia;
using Murmur.Core.Platform;

namespace Murmur.App.Views;

/// <summary>The maths of the floating orb's input region (#46): which pixels of its fixed-size window take clicks.</summary>
internal static class FloatingOrbInputRegion
{
    /// <summary>
    /// One shape's part of the region in pixels: its bounds (DIPs, in the window) grown by the shadow margin, kept inside the window,
    /// scaled and rounded outwards so the edge pixels of the shape always take the click.
    /// </summary>
    public static FloatingWindowRect ToPixels(Rect shape, Thickness margin, Rect window, double scale)
    {
        var grown = new Rect(
            shape.X - margin.Left,
            shape.Y - margin.Top,
            shape.Width + margin.Left + margin.Right,
            shape.Height + margin.Top + margin.Bottom);
        var kept = grown.Intersect(window);
        if (kept.Width <= 0 || kept.Height <= 0)
        {
            return default;
        }

        var left = (int)Math.Floor(kept.X * scale);
        var top = (int)Math.Floor(kept.Y * scale);
        var right = (int)Math.Ceiling(kept.Right * scale);
        var bottom = (int)Math.Ceiling(kept.Bottom * scale);
        return new FloatingWindowRect(left, top, right - left, bottom - top);
    }
}
