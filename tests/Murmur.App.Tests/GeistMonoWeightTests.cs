using Avalonia;
using Avalonia.Media;

namespace Murmur.App.Tests;

/// <summary>The recording timer is Geist Mono at weight 300 on the canvas: the font family must resolve both the Regular and the Light face.</summary>
public sealed class GeistMonoWeightTests
{
    [Fact]
    public async Task TheMonoFamily_ResolvesBothTheRegularAndTheLightFace()
    {
        var (regular, light) = await UiHarness.Session.Dispatch(
            () =>
            {
                Application.Current!.TryGetResource("Font.Mono", null, out var family);
                var mono = (FontFamily)family!;
                var manager = FontManager.Current;
                Assert.True(manager.TryGetGlyphTypeface(new Typeface(mono, FontStyle.Normal, FontWeight.Normal), out var normal));
                Assert.True(manager.TryGetGlyphTypeface(new Typeface(mono, FontStyle.Normal, FontWeight.Light), out var thin));
                return (normal!.Weight, thin!.Weight);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(FontWeight.Normal, regular);
        Assert.Equal(FontWeight.Light, light);
    }
}
