using System.Globalization;
using System.Xml.Linq;
using Murmur.App.Controls;

namespace Murmur.App.Tests;

public sealed class WaveformOverhangTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheSessionViewMargin_MatchesTheOverhangOfTheWaveformControl()
    {
        // The control draws its playhead Overhang px above and below the bars; the view pulls it up and down by the same amount so the bars keep their place.
        var document = XDocument.Load(Path.Combine(RepoPaths.AppDirectory, "Views", "SessionView.axaml"));
        var margin = document.Descendants()
            .Single(e => (string?)e.Attribute(Xaml + "Key") == "Session.WaveformMargin").Value
            .Split(',')
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();

        // "0,-3" is horizontal, vertical: nothing at the sides, the overhang above and below.
        Assert.Equal([0, -WaveformControl.Overhang], margin);
    }
}
