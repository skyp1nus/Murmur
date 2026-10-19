namespace Murmur.App.Services;

/// <summary>
/// A source of "show the main window" requests that come from outside the UI: a second start of the program hands over to the
/// running one (single instance, built by the composition root).
/// </summary>
public interface IActivationRequests
{
    /// <summary>Someone asked for the window. Raised on any thread, not the UI thread; handlers post to the UI thread and return.</summary>
    event Action? Requested;

    /// <summary>
    /// The window was brought to the front: tells the requester it can exit. Not called when the window is closing or gone, so
    /// the requester keeps waiting and takes over once this process has ended. Any thread.
    /// </summary>
    void Acknowledge();
}
