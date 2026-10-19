using System.Collections.Frozen;
using System.Text;

namespace Murmur.Core.Input;

/// <summary>
/// A global hotkey: modifiers plus one key. The text syntax is the one Avalonia's <c>KeyGesture</c> prints and parses
/// (the Settings screen stores that string): <c>Ctrl+Shift+R</c>, <c>Ctrl+Alt+F9</c>, <c>Ctrl+D1</c> (digit key),
/// <c>Ctrl+Alt+Oem3</c>. <see cref="ToString"/> uses Avalonia's canonical names and order (Ctrl, Shift, Alt, Cmd);
/// <see cref="TryParse"/> additionally accepts, case-insensitively, <c>Control</c>, <c>Meta</c>/<c>Win</c> for
/// <c>Cmd</c>, whitespace around <c>+</c>, and the Avalonia key aliases (<c>OemTilde</c>, <c>Oem1</c>, <c>Snapshot</c>,
/// <c>Next</c>, ...). Bare digits (<c>Ctrl+1</c>) are refused: Avalonia does not read them as the digit key either.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, HotkeyKey Key)
{
    private static readonly FrozenDictionary<string, HotkeyKey> Keys = BuildKeys();

    /// <summary>
    /// Parses <paramref name="text"/>. Refused: empty text, no key or more than one key, a repeated modifier (also under
    /// two aliases), an unknown token, and a gesture without Ctrl, Alt or Meta (Shift alone or no modifier would steal
    /// normal typing system-wide).
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        HotkeyKey? key = null;
        foreach (var range in text.AsSpan().Split('+'))
        {
            var token = text.AsSpan(range).Trim();
            if (token.IsEmpty)
            {
                return false;
            }

            if (TryParseModifier(token, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
            }
            else if (Keys.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(token, out var parsed))
            {
                if (key is not null)
                {
                    return false;
                }

                key = parsed;
            }
            else
            {
                return false;
            }
        }

        if (key is null || (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Meta)) == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    /// <summary>The gesture in the settings syntax; <c>TryParse(ToString())</c> returns the same gesture.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(24);
        AppendModifier(text, Modifiers, HotkeyModifiers.Ctrl, "Ctrl");
        AppendModifier(text, Modifiers, HotkeyModifiers.Shift, "Shift");
        AppendModifier(text, Modifiers, HotkeyModifiers.Alt, "Alt");
        AppendModifier(text, Modifiers, HotkeyModifiers.Meta, "Cmd");
        if (text.Length > 0)
        {
            text.Append('+');
        }

        return text.Append(KeyName(Key)).ToString();
    }

    private static void AppendModifier(StringBuilder text, HotkeyModifiers modifiers, HotkeyModifiers flag, string name)
    {
        if ((modifiers & flag) == 0)
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append('+');
        }

        text.Append(name);
    }

    /// <summary>The canonical text of <paramref name="key"/> (what <see cref="ToString"/> prints).</summary>
    public static string KeyName(HotkeyKey key) => key switch
    {
        HotkeyKey.OemTilde => "Oem3",
        HotkeyKey.OemOpenBrackets => "Oem4",
        _ => key.ToString(),
    };

    private static bool TryParseModifier(ReadOnlySpan<char> token, out HotkeyModifiers modifier)
    {
        if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Ctrl;
        }
        else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Alt;
        }
        else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Shift;
        }
        else if (token.Equals("Cmd", StringComparison.OrdinalIgnoreCase)
                 || token.Equals("Meta", StringComparison.OrdinalIgnoreCase)
                 || token.Equals("Win", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Meta;
        }
        else
        {
            modifier = HotkeyModifiers.None;
            return false;
        }

        return true;
    }

    private static FrozenDictionary<string, HotkeyKey> BuildKeys()
    {
        var map = new Dictionary<string, HotkeyKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Enum.GetValues<HotkeyKey>())
        {
            map[KeyName(key)] = key;
            map[key.ToString()] = key;
        }

        // Aliases Avalonia's KeyGesture.Parse understands for the same keys (Key enum synonyms).
        map["Snapshot"] = HotkeyKey.PrintScreen;
        map["Next"] = HotkeyKey.PageDown;
        map["Oem1"] = HotkeyKey.OemSemicolon;
        map["Oem2"] = HotkeyKey.OemQuestion;
        map["Oem5"] = HotkeyKey.OemPipe;
        map["Oem6"] = HotkeyKey.OemCloseBrackets;
        map["Oem7"] = HotkeyKey.OemQuotes;
        map["Oem102"] = HotkeyKey.OemBackslash;
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
