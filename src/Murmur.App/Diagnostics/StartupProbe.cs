using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Murmur.App.Diagnostics;

/// <summary>
/// Cold-start measurement used by CI. When the process is started with <c>--startup-probe &lt;path&gt;</c> the app
/// writes <c>{"startupMs":..., "utc":"..."}</c> to <c>path</c> once the main window has rendered its first frame
/// and then exits with code 0. Without the flag nothing changes.
/// </summary>
public static class StartupProbe
{
    public const string Flag = "--startup-probe";

    /// <summary>Finds <c>--startup-probe &lt;path&gt;</c> in <paramref name="args"/>.</summary>
    public static bool TryGetPath(IReadOnlyList<string> args, out string path)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], Flag, StringComparison.Ordinal) && args[i + 1].Length > 0)
            {
                path = args[i + 1];
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>The JSON payload, hand-written so no serializer (and no reflection) is involved.</summary>
    public static string FormatJson(double startupMs, DateTimeOffset utc) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"startupMs\":{startupMs:0.###},\"utc\":\"{utc.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}");

    /// <summary>
    /// Arms the probe: after <paramref name="window"/> is opened, waits for two animation frames (the first one
    /// is rendered by the time the second callback runs), writes the result and calls <paramref name="exit"/>
    /// with 0, or with 1 when the file cannot be written.
    /// </summary>
    public static void Attach(Window window, string path, Action<int> exit)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(exit);

        window.Opened += OnOpened;

        void OnOpened(object? sender, EventArgs e)
        {
            window.Opened -= OnOpened;
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Complete(path, exit)));
        }
    }

    private static void Complete(string path, Action<int> exit)
    {
        var code = 0;
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var process = Process.GetCurrentProcess();
            var startupMs = (now.UtcDateTime - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            File.WriteAllText(path, FormatJson(startupMs, now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            code = 1;
        }

        Dispatcher.UIThread.Post(() => exit(code));
    }
}
