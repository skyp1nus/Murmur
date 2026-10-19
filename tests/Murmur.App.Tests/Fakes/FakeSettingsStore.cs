using Murmur.Core.Settings;

namespace Murmur.App.Tests.Fakes;

/// <summary>In-memory <see cref="ISettingsStore"/>: saving replaces <see cref="Current"/> and raises <see cref="Changed"/> on the caller's thread.</summary>
internal sealed class FakeSettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public event Action<AppSettings>? Changed;

    public int SaveCount { get; private set; }

    public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Current = settings;
        SaveCount++;
        Changed?.Invoke(settings);
        return ValueTask.CompletedTask;
    }

    /// <summary>Number of handlers currently subscribed to <see cref="Changed"/>.</summary>
    public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;
}
