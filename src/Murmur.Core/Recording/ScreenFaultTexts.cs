using Murmur.Core.Capture;

namespace Murmur.Core.Recording;

/// <summary>Short Ukrainian texts the user sees for the faults of the screen recorder (the detail goes to the console).</summary>
internal static class ScreenFaultTexts
{
    internal const string PoolExhausted = "Запис екрана пропустив кадри: компʼютер не встигав.";

    internal const string CpuReadback = "Екран записується без прискорення відеокарти — це більше навантажує процесор.";

    internal const string OtherNonFatal = "Під час запису екрана були збої.";

    internal const string Stopped = "Запис екрана зупинився через помилку; звук записується далі.";

    /// <summary>The user text of a non-fatal fault; the exhaustion of the encoder's sample pool is told apart from the CPU fallback by its message.</summary>
    internal static string Describe(CaptureFault fault)
    {
        var message = fault.Message;
        if (message.Contains("stayed in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("pool", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ran dry", StringComparison.OrdinalIgnoreCase))
        {
            return PoolExhausted;
        }

        return message.Contains("CPU readback", StringComparison.OrdinalIgnoreCase) ? CpuReadback : OtherNonFatal;
    }
}
