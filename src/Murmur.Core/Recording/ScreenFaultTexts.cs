using Murmur.Core.Capture;

namespace Murmur.Core.Recording;

/// <summary>Short Ukrainian texts the user sees for the faults of the screen recorder (the detail goes to the console).</summary>
internal static class ScreenFaultTexts
{
    internal const string PoolExhausted = "Запис екрана пропустив кадри: компʼютер не встигав.";

    internal const string CpuReadback = "Екран записується без прискорення відеокарти — це більше навантажує процесор.";

    internal const string OtherNonFatal = "Під час запису екрана були збої.";

    internal const string Stopped = "Запис екрана зупинився через помилку; звук записується далі.";

    /// <summary>The session text of a non-fatal fault of the given kind.</summary>
    internal static string Describe(CaptureFaultKind kind) => kind switch
    {
        CaptureFaultKind.FramesDropped => PoolExhausted,
        CaptureFaultKind.CpuFallback => CpuReadback,
        _ => OtherNonFatal,
    };
}
