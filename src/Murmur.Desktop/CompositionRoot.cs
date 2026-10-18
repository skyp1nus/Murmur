namespace Murmur.Desktop;

/// <summary>
/// Hand-written composition root (pure DI): every service is built here with plain constructors.
/// No container, no reflection, so the object graph stays Native-AOT friendly and readable.
/// </summary>
internal sealed class CompositionRoot
{
    public CompositionRoot(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Args = args;
    }

    /// <summary>Command-line arguments the process was started with.</summary>
    public IReadOnlyList<string> Args { get; }

    public App.App CreateApp()
    {
#if WINDOWS
        // Windows capture (Murmur.Capture.Windows) is constructed and wired here.
#endif
        return new App.App(Args);
    }
}
