using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Platform;

/// <summary>Win32 imports of the reduce-motion preference (user32), source-generated; signatures follow the Windows SDK 10.0.26100 headers.</summary>
internal static unsafe partial class MotionNativeMethods
{
    /// <summary><c>SPI_GETCLIENTAREAANIMATION</c>: the "Animation effects" switch of Settings → Accessibility → Visual effects (a <c>BOOL</c>).</summary>
    public const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary><c>WM_SETTINGCHANGE</c>: broadcast to top-level windows when a system parameter changes.</summary>
    public const uint WmSettingChange = 0x001A;

    /// <summary><c>BOOL SystemParametersInfoW(UINT uiAction, UINT uiParam, PVOID pvParam, UINT fWinIni)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static partial int SystemParametersInfoW(uint action, uint param, int* value, uint winIni);

    /// <summary>
    /// Whether the system plays animations in windows (Settings → Accessibility → Visual effects → Animation effects). A failed
    /// call counts as "on": a PC that cannot answer is not asking for less motion.
    /// </summary>
    public static bool ClientAreaAnimationEnabled()
    {
        var enabled = 1;
        return SystemParametersInfoW(SpiGetClientAreaAnimation, 0, &enabled, 0) == 0 || enabled != 0;
    }
}
