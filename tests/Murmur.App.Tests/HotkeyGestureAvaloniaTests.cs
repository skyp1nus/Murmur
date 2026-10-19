using Avalonia.Input;
using Murmur.Core.Input;

namespace Murmur.App.Tests;

/// <summary>
/// The Settings screen stores Avalonia <c>KeyGesture</c> strings and Core parses them for the global hotkeys: both sides
/// must agree on every key name and modifier.
/// </summary>
public sealed class HotkeyGestureAvaloniaTests
{
    private static readonly KeyModifiers[] AvaloniaCombos =
    [
        KeyModifiers.Control,
        KeyModifiers.Alt,
        KeyModifiers.Meta,
        KeyModifiers.Control | KeyModifiers.Shift,
        KeyModifiers.Control | KeyModifiers.Alt,
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift,
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta,
    ];

    private static Key AvaloniaKey(HotkeyKey key) => Enum.Parse<Key>(HotkeyGesture.KeyName(key));

    private static HotkeyModifiers CoreModifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? HotkeyModifiers.Ctrl : 0)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? HotkeyModifiers.Alt : 0)
        | (modifiers.HasFlag(KeyModifiers.Shift) ? HotkeyModifiers.Shift : 0)
        | (modifiers.HasFlag(KeyModifiers.Meta) ? HotkeyModifiers.Meta : 0);

    [Fact]
    public void EveryKey_CoreString_RoundTripsThroughAvaloniaUnchanged()
    {
        foreach (var key in Enum.GetValues<HotkeyKey>())
        {
            var text = new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, key).ToString();

            var avalonia = KeyGesture.Parse(text);

            Assert.Equal(AvaloniaKey(key), avalonia.Key);
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, avalonia.KeyModifiers);
            Assert.Equal(text, avalonia.ToString());
        }
    }

    [Fact]
    public void EveryKey_AvaloniaString_ParsesInCoreToTheSameGesture()
    {
        foreach (var key in Enum.GetValues<HotkeyKey>())
        {
            foreach (var combo in AvaloniaCombos)
            {
                var text = new KeyGesture(AvaloniaKey(key), combo).ToString();

                Assert.True(HotkeyGesture.TryParse(text, out var gesture), text);
                Assert.Equal(new HotkeyGesture(CoreModifiers(combo), key), gesture);
            }
        }
    }

    [Fact]
    public void ModifierCombinations_AgreeWithAvalonia()
    {
        foreach (var combo in AvaloniaCombos)
        {
            var core = new HotkeyGesture(CoreModifiers(combo), HotkeyKey.F9);

            var avalonia = KeyGesture.Parse(core.ToString());

            Assert.Equal(combo, avalonia.KeyModifiers);
            Assert.Equal(Key.F9, avalonia.Key);
            if (!combo.HasFlag(KeyModifiers.Meta))
            {
                // The Meta modifier is spelled "Cmd" here; Avalonia may spell it per platform, so it is compared by value above.
                Assert.Equal(core.ToString(), avalonia.ToString());
            }
        }
    }

    [Fact]
    public void BareExtendedFunctionKeys_RoundTripThroughAvalonia()
    {
        for (var key = HotkeyKey.F13; key <= HotkeyKey.F24; key++)
        {
            var text = new HotkeyGesture(HotkeyModifiers.None, key).ToString();

            Assert.Equal(key.ToString(), text);
            Assert.Equal(text, KeyGesture.Parse(text).ToString());
            Assert.True(HotkeyGesture.TryParse(new KeyGesture(AvaloniaKey(key)).ToString(), out var parsed));
            Assert.Equal(new HotkeyGesture(HotkeyModifiers.None, key), parsed);
        }
    }

    [Fact]
    public void CommandSymbol_IsMeta_ForCoreAndAvalonia()
    {
        Assert.True(HotkeyGesture.TryParse("\u2318+E", out var gesture));
        Assert.Equal(HotkeyModifiers.Meta, gesture.Modifiers);
        Assert.Equal(KeyModifiers.Meta, KeyGesture.Parse("\u2318+E").KeyModifiers);
    }

    [Theory]
    [InlineData("Control+Shift+R", "Ctrl+Shift+R")]
    [InlineData("Win+E", "Cmd+E")]
    [InlineData("Meta+E", "Cmd+E")]
    [InlineData("Ctrl+Snapshot", "Ctrl+PrintScreen")]
    [InlineData("Ctrl+Next", "Ctrl+PageDown")]
    [InlineData("Ctrl+OemTilde", "Ctrl+Oem3")]
    [InlineData("Ctrl+OemOpenBrackets", "Ctrl+Oem4")]
    [InlineData("Ctrl+Oem1", "Ctrl+OemSemicolon")]
    public void Aliases_MeanTheSameKeyToCoreAndAvalonia(string alias, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(alias, out var gesture));
        Assert.Equal(canonical, gesture.ToString());

        var avalonia = KeyGesture.Parse(alias);
        Assert.True(HotkeyGesture.TryParse(canonical, out var canonicalGesture));
        Assert.Equal(AvaloniaKey(canonicalGesture.Key), avalonia.Key);
        Assert.Equal(CoreModifiers(avalonia.KeyModifiers), gesture.Modifiers);
    }
}
