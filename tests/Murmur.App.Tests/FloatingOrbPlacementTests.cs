using Murmur.App.ViewModels;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>Where the floating orb goes on a set of screens (#46): the default corner, a saved spot, an unplugged monitor, mixed scaling.</summary>
public sealed class FloatingOrbPlacementTests
{
    private static readonly WorkArea Left = new(0, 0, 1920, 1040, 1.0);
    private static readonly WorkArea Right = new(1920, 0, 1920, 1080, 1.0);

    [Fact]
    public void NothingSaved_IsTheCornerOfTheFirstWorkArea_NotOfTheOthers()
    {
        var placement = FloatingOrbPlacement.Resolve(null, [Left, Right]);

        Assert.Equal(new OrbPlacement(1852, 972, Corrected: false), placement);
    }

    [Fact]
    public void ASecondaryMonitorLeftOfThePrimary_HasNegativeCoordinates_AndIsKept()
    {
        var secondary = new WorkArea(-1920, 0, 1920, 1080, 1.0);

        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(-1500, 200), [Left, secondary]);

        Assert.Equal(new OrbPlacement(-1500, 200, Corrected: false), placement);
    }

    [Fact]
    public void ASpotOnTheSecondMonitor_StaysThere_WhileItIsConnected()
    {
        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(2500, 700), [Left, Right]);

        Assert.Equal(new OrbPlacement(2500, 700, Corrected: false), placement);
    }

    [Fact]
    public void WhenItsMonitorIsGone_TheSpotMovesToTheNearestOne()
    {
        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(2500, 700), [Left]);

        Assert.Equal(new OrbPlacement(1920 - 44, 700, Corrected: true), placement);
    }

    [Fact]
    public void WithTwoMonitorsLeft_TheNearerOneIsChosen()
    {
        var above = new WorkArea(0, -1080, 1920, 1080, 1.0);

        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(900, -1500), [Left, above]);

        Assert.Equal(new OrbPlacement(900, -1080, Corrected: true), placement);
    }

    [Fact]
    public void TheSavedNumbers_AreDipsOfTheScreen_SoAScreenThatChangedItsScalingKeepsTheSpot()
    {
        // Saved at 100 % on x = 800; the same screen now runs at 150 %: the orb is at 1200 px, still 800 DIPs in.
        var scaled = new WorkArea(0, 0, 2880, 1560, 1.5);

        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(800, 600), [scaled]);

        Assert.Equal(new OrbPlacement(1200, 900, Corrected: false), placement);
    }

    [Fact]
    public void OnMixedScalingScreens_TheSavedNumbersAreDipsOfThePrimary_SoNoSpotIsAmbiguous()
    {
        var primary = new WorkArea(0, 0, 3000, 1560, 2.0);
        var plain = new WorkArea(3000, 0, 1920, 1080, 1.0);

        // An orb at 3400 px on the 100 % monitor is 1700 DIPs of the primary, a spot that does not exist on the 200 % monitor itself.
        var saved = FloatingOrbPlacement.ToSaved(3400, 200, [primary, plain]);
        var back = FloatingOrbPlacement.Resolve(saved, [primary, plain]);

        Assert.Equal(new FloatingOrbPosition(1700, 100), saved);
        Assert.Equal(new OrbPlacement(3400, 200, Corrected: false), back);
    }

    [Fact]
    public void TheOrbIsAsBigAsTheScreenItIsOn_WhenItIsPulledBack()
    {
        var primary = new WorkArea(0, 0, 3000, 1560, 2.0);
        var plain = new WorkArea(3000, 0, 1920, 1080, 1.0);

        // Saved far to the right of the plain monitor: it ends against that monitor's edge, 44 px (not 88) in.
        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(9000, 100), [primary, plain]);

        Assert.Equal(new OrbPlacement(3000 + 1920 - 44, 200, Corrected: true), placement);
    }

    [Fact]
    public void ToSaved_UsesThePrimaryScaling_WhereverTheOrbIs()
    {
        var hiDpi = new WorkArea(0, 0, 3000, 1560, 2.0);

        var saved = FloatingOrbPlacement.ToSaved(4000, 100, [hiDpi]);

        Assert.Equal(new FloatingOrbPosition(2000, 50), saved);
    }

    [Fact]
    public void ToSaved_WithoutScreens_SavesNothing()
    {
        Assert.Null(FloatingOrbPlacement.ToSaved(10, 10, []));
        Assert.Null(FloatingOrbPlacement.Resolve(new FloatingOrbPosition(10, 10), []));
    }

    [Theory]
    [InlineData(double.NaN, 5)]
    [InlineData(5, double.PositiveInfinity)]
    public void ANonsenseFile_FallsBackToTheCorner(double x, double y)
    {
        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(x, y), [Left]);

        Assert.Equal(new OrbPlacement(1852, 972, Corrected: false), placement);
    }

    [Fact]
    public void AWorkAreaSmallerThanTheOrb_StillGivesAPlace()
    {
        var tiny = new WorkArea(0, 0, 30, 30, 1.0);

        var placement = FloatingOrbPlacement.Resolve(new FloatingOrbPosition(900, 900), [tiny]);

        Assert.Equal(new OrbPlacement(0, 0, Corrected: true), placement);
    }
}
