using Murmur.Core.Settings;

namespace Murmur.App.Services;

/// <summary>Settings that live only for the run: used when the composition root hands over no store (designer, tests).</summary>
internal sealed class InMemorySettingsStore : ISettingsStore
{
    private volatile AppSettings _current = new();

    public AppSettings Current => _current;

    public event Action<AppSettings>? Changed;

    public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
        Changed?.Invoke(settings);
        return ValueTask.CompletedTask;
    }
}
