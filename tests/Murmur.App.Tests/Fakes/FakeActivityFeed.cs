using Murmur.Core.Activity;

namespace Murmur.App.Tests.Fakes;

/// <summary>An <see cref="IActivityFeed"/> the test sets; <see cref="Set"/> raises <see cref="Changed"/> synchronously.</summary>
internal sealed class FakeActivityFeed : IActivityFeed
{
    public IReadOnlyList<ActivityItem> Current { get; private set; } = [];

    public event Action? Changed;

    public void Set(params ActivityItem[] items)
    {
        Current = items;
        Changed?.Invoke();
    }

    public bool HasSubscribers => Changed is not null;
}
