namespace Murmur.App.Tests.Design;

/// <summary>
/// Layout checks that read the geometry of a whole window run alone. All UI tests share one headless Avalonia session and
/// one UI thread; an <c>await</c> inside a scene lets the UI jobs of other tests run in between (their timers, pumps and
/// layout passes), and on a loaded machine that interleaving moved a control by a pixel and failed a fit check at random.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveUiCollection
{
    public const string Name = "ExclusiveUi";
}
