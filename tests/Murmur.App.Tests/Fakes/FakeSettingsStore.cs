using Murmur.Core.Settings;

namespace Murmur.App.Tests.Fakes;

/// <summary>In-memory <see cref="ISettingsStore"/>: saving replaces <see cref="Current"/> and raises <see cref="Changed"/> on the caller's thread.</summary>
internal sealed class FakeSettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public event Action<AppSettings>? Changed;

    public int SaveCount { get; private set; }

    /// <summary>When set, <see cref="SaveAsync"/> throws it (nothing is stored).</summary>
    public Exception? SaveFailure { get; set; }

    public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (SaveFailure is { } failure)
        {
            throw failure;
        }

        Current = settings;
        SaveCount++;
        Changed?.Invoke(settings);
        return ValueTask.CompletedTask;
    }

    /// <summary>Raises <see cref="Changed"/> without touching <see cref="Current"/> (an event that arrives late or out of order).</summary>
    public void Raise(AppSettings settings) => Changed?.Invoke(settings);

    /// <summary>Replaces <see cref="Current"/> without raising <see cref="Changed"/>.</summary>
    public void SetCurrentSilently(AppSettings settings) => Current = settings;

    /// <summary>Number of handlers currently subscribed to <see cref="Changed"/>.</summary>
    public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;
}
