namespace Murmur.Core.Tests.Recording;

/// <summary>
/// Tests that drive real capture/writer threads against the wall clock. They run on their own, after the parallel
/// tests, so CPU-heavy tests (ring stress, hour-long resampling) cannot starve them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealtimeCollection
{
    public const string Name = "Realtime";
}
