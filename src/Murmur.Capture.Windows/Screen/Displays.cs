using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Screen;

/// <summary>A connected display and its monitor handle (valid until the display configuration changes).</summary>
internal sealed record DisplayEntry(nint Monitor, DisplayInfo Info);

/// <summary>
/// Display enumeration through EnumDisplayMonitors + GetMonitorInfoW. Ids are the GDI device names
/// (<c>\\.\DISPLAY1</c>), sizes are the current mode in physical pixels (EnumDisplaySettingsW, independent of the
/// process DPI awareness), names come from the monitor's device string (EnumDisplayDevicesW).
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal static unsafe class Displays
{
    public static IReadOnlyList<DisplayEntry> Enumerate()
    {
        var monitors = new List<nint>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            if (ScreenNativeMethods.EnumDisplayMonitors(0, null, &OnMonitor, GCHandle.ToIntPtr(handle)) == 0)
            {
                throw new COMException("EnumDisplayMonitors failed.", Marshal.GetHRForLastWin32Error());
            }
        }
        finally
        {
            handle.Free();
        }

        var result = new List<DisplayEntry>(monitors.Count);
        foreach (var monitor in monitors)
        {
            if (Describe(monitor) is { } info)
            {
                result.Add(new DisplayEntry(monitor, info));
            }
        }

        return result;
    }

    /// <summary>The display with <paramref name="displayId"/> (case-insensitive), or the primary display when null.</summary>
    /// <exception cref="ArgumentException">No display has that id.</exception>
    /// <exception cref="InvalidOperationException">No display is connected.</exception>
    public static DisplayEntry Find(string? displayId)
    {
        var displays = Enumerate();
        if (displays.Count == 0)
        {
            throw new InvalidOperationException("No display is connected.");
        }

        if (displayId is null)
        {
            foreach (var display in displays)
            {
                if (display.Info.IsPrimary)
                {
                    return display;
                }
            }

            return displays[0];
        }

        foreach (var display in displays)
        {
            if (string.Equals(display.Info.Id, displayId, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }

        throw new ArgumentException($"No connected display has the id '{displayId}'.", nameof(displayId));
    }

    private static DisplayInfo? Describe(nint monitor)
    {
        MonitorInfoExW info = default;
        info.Size = (uint)sizeof(MonitorInfoExW);
        if (ScreenNativeMethods.GetMonitorInfoW(monitor, &info) == 0)
        {
            return null;
        }

        var deviceName = ReadString(info.Device, MonitorInfoExW.DeviceNameChars);

        var width = info.Monitor.Right - info.Monitor.Left;
        var height = info.Monitor.Bottom - info.Monitor.Top;
        string? monitorName = null;
        fixed (char* pDevice = deviceName)
        {
            DevModeW mode = default;
            mode.Size = DevModeW.NativeSize;
            if (ScreenNativeMethods.EnumDisplaySettingsW(pDevice, ScreenConstants.EnumCurrentSettings, &mode) != 0
                && mode.PelsWidth > 0 && mode.PelsHeight > 0)
            {
                width = (int)mode.PelsWidth;
                height = (int)mode.PelsHeight;
            }

            DisplayDeviceW device = default;
            device.Size = (uint)sizeof(DisplayDeviceW);
            if (ScreenNativeMethods.EnumDisplayDevicesW(pDevice, 0, &device, 0) != 0)
            {
                monitorName = ReadString(device.DeviceString, 128);
            }
        }

        return new DisplayInfo(
            deviceName,
            DisplayNaming.Format(deviceName, monitorName),
            width,
            height,
            (info.Flags & ScreenConstants.MonitorInfoFlagPrimary) != 0);
    }

    private static string ReadString(char* chars, int capacity)
    {
        var span = new ReadOnlySpan<char>(chars, capacity);
        var end = span.IndexOf('\0');
        return new string(end >= 0 ? span[..end] : span);
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, Rect32* rect, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<nint> list)
        {
            list.Add(monitor);
        }

        return 1;
    }
}

/// <summary>User-facing display names (pure).</summary>
internal static class DisplayNaming
{
    /// <summary>
    /// "Display 2 (DELL U2720Q)" from <c>\\.\DISPLAY2</c> and the monitor's device string; "Display 2" when the
    /// device string is empty; the raw device name when it carries no number.
    /// </summary>
    public static string Format(string deviceName, string? monitorName)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        var digits = 0;
        while (digits < deviceName.Length && char.IsAsciiDigit(deviceName[deviceName.Length - 1 - digits]))
        {
            digits++;
        }

        var label = digits > 0 ? "Display " + deviceName[^digits..].TrimStart('0') : deviceName.TrimStart('\\', '.');
        if (label == "Display ")
        {
            label = "Display 0";
        }

        var monitor = monitorName?.Trim();
        return string.IsNullOrEmpty(monitor) ? label : $"{label} ({monitor})";
    }
}
