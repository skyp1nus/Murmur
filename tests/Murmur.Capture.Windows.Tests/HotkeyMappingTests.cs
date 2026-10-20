using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Input;
using Murmur.Capture.Windows.Input.Interop;
using Murmur.Core.Input;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The pure parts of the global-hotkey code, on any OS: the VK_* / MOD_* tables (values copied from winuser.h of the
/// Windows SDK 10.0.26100), the per-action id / duplicate book and the interop struct layouts.
/// </summary>
public sealed class HotkeyMappingTests
{
    [Fact]
    public void EveryHotkeyKey_HasAUniqueVirtualKey()
    {
        var codes = new Dictionary<ushort, HotkeyKey>();
        foreach (var key in Enum.GetValues<HotkeyKey>())
        {
            var vk = HotkeyKeyMap.ToVirtualKey(key);
            Assert.True(vk is > 0 and < 0xFF, $"{key} -> 0x{vk:X}");
            Assert.True(codes.TryAdd(vk, key), $"{key} and {codes.GetValueOrDefault(vk)} share VK 0x{vk:X}");
        }
    }

    [Theory]
    [InlineData(HotkeyKey.A, 0x41)]
    [InlineData(HotkeyKey.R, 0x52)]
    [InlineData(HotkeyKey.Z, 0x5A)]
    [InlineData(HotkeyKey.D0, 0x30)]
    [InlineData(HotkeyKey.D9, 0x39)]
    [InlineData(HotkeyKey.F1, 0x70)]
    [InlineData(HotkeyKey.F9, 0x78)]
    [InlineData(HotkeyKey.F12, 0x7B)]
    [InlineData(HotkeyKey.F13, 0x7C)]
    [InlineData(HotkeyKey.F24, 0x87)]
    [InlineData(HotkeyKey.NumPad0, 0x60)]
    [InlineData(HotkeyKey.NumPad9, 0x69)]
    [InlineData(HotkeyKey.Space, 0x20)]
    [InlineData(HotkeyKey.PageUp, 0x21)] // VK_PRIOR
    [InlineData(HotkeyKey.PageDown, 0x22)] // VK_NEXT
    [InlineData(HotkeyKey.End, 0x23)]
    [InlineData(HotkeyKey.Home, 0x24)]
    [InlineData(HotkeyKey.Left, 0x25)]
    [InlineData(HotkeyKey.Up, 0x26)]
    [InlineData(HotkeyKey.Right, 0x27)]
    [InlineData(HotkeyKey.Down, 0x28)]
    [InlineData(HotkeyKey.PrintScreen, 0x2C)] // VK_SNAPSHOT
    [InlineData(HotkeyKey.Insert, 0x2D)]
    [InlineData(HotkeyKey.Delete, 0x2E)]
    [InlineData(HotkeyKey.Pause, 0x13)]
    [InlineData(HotkeyKey.OemSemicolon, 0xBA)] // VK_OEM_1
    [InlineData(HotkeyKey.OemPlus, 0xBB)]
    [InlineData(HotkeyKey.OemComma, 0xBC)]
    [InlineData(HotkeyKey.OemMinus, 0xBD)]
    [InlineData(HotkeyKey.OemPeriod, 0xBE)]
    [InlineData(HotkeyKey.OemQuestion, 0xBF)] // VK_OEM_2
    [InlineData(HotkeyKey.OemTilde, 0xC0)] // VK_OEM_3
    [InlineData(HotkeyKey.OemOpenBrackets, 0xDB)] // VK_OEM_4
    [InlineData(HotkeyKey.OemPipe, 0xDC)] // VK_OEM_5
    [InlineData(HotkeyKey.OemCloseBrackets, 0xDD)] // VK_OEM_6
    [InlineData(HotkeyKey.OemQuotes, 0xDE)] // VK_OEM_7
    [InlineData(HotkeyKey.Oem8, 0xDF)]
    [InlineData(HotkeyKey.OemBackslash, 0xE2)] // VK_OEM_102
    public void VirtualKeys_MatchWinuserHeader(HotkeyKey key, int expected) =>
        Assert.Equal((ushort)expected, HotkeyKeyMap.ToVirtualKey(key));

    [Fact]
    public void ToVirtualKey_RefusesUndefinedKeys() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyKeyMap.ToVirtualKey((HotkeyKey)9999));

    [Theory]
    [InlineData(HotkeyModifiers.Ctrl, 0x4002u)]
    [InlineData(HotkeyModifiers.Alt, 0x4001u)]
    [InlineData(HotkeyModifiers.Shift, 0x4004u)]
    [InlineData(HotkeyModifiers.Meta, 0x4008u)]
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x4003u)]
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta, 0x400Fu)]
    public void Modifiers_MapToModFlags_WithNoRepeat(HotkeyModifiers modifiers, uint expected) =>
        Assert.Equal(expected, HotkeyKeyMap.ToNativeModifiers(modifiers));

    [Fact]
    public void ActionIds_AreUniqueAndRoundTrip()
    {
        var ids = new HashSet<int>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var id = HotkeyBook.IdOf(action);
            Assert.True(id > 0);
            Assert.True(ids.Add(id));
            Assert.True(HotkeyBook.TryActionOf(id, out var back));
            Assert.Equal(action, back);
        }

        Assert.False(HotkeyBook.TryActionOf(0, out _));
        Assert.False(HotkeyBook.TryActionOf(ids.Count + 1, out _));
        Assert.False(HotkeyBook.TryActionOf(-1, out _));
    }

    [Fact]
    public void Book_DetectsTheSameGestureUnderAnotherAction()
    {
        var book = new HotkeyBook();
        var gesture = new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.F9);
        book.Set(HotkeyAction.ToggleRecording, gesture);

        Assert.False(book.IsHeldByOther(HotkeyAction.ToggleRecording, gesture)); // re-registering its own gesture is fine
        Assert.True(book.IsHeldByOther(HotkeyAction.ShowApp, gesture));
        Assert.False(book.IsHeldByOther(HotkeyAction.ShowApp, gesture with { Key = HotkeyKey.F10 }));
        Assert.False(book.IsHeldByOther(HotkeyAction.ShowApp, gesture with { Modifiers = HotkeyModifiers.Ctrl }));

        book.Remove(HotkeyAction.ToggleRecording);
        Assert.False(book.IsHeldByOther(HotkeyAction.ShowApp, gesture));
        Assert.False(book.TryGet(HotkeyAction.ToggleRecording, out _));
    }

    [Fact]
    public void EveryAction_HasItsOwnWindowsHotkeyId_ThatMapsBack_TogglePauseIncluded()
    {
        var ids = new HashSet<int>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var id = HotkeyBook.IdOf(action);
            Assert.True(ids.Add(id), $"{action} shares its id");
            Assert.True(HotkeyBook.TryActionOf(id, out var back));
            Assert.Equal(action, back);
        }

        Assert.Contains(HotkeyBook.IdOf(HotkeyAction.TogglePause), ids);
    }

    [Fact]
    public void Book_KeepsTheGesturesOfAllFourActionsApart()
    {
        var book = new HotkeyBook();
        var pause = new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyKey.P);
        book.Set(HotkeyAction.TogglePause, pause);

        Assert.True(book.IsHeldByOther(HotkeyAction.ToggleRecording, pause));
        Assert.False(book.IsHeldByOther(HotkeyAction.TogglePause, pause));
        Assert.Equal([HotkeyAction.TogglePause], book.HeldActions().ToArray());
    }

    [Fact]
    public void Book_TracksHeldActions()
    {
        var book = new HotkeyBook();
        var a = new HotkeyGesture(HotkeyModifiers.Ctrl, HotkeyKey.A);
        var b = new HotkeyGesture(HotkeyModifiers.Alt, HotkeyKey.B);
        book.Set(HotkeyAction.ToggleScreen, a);
        book.Set(HotkeyAction.ShowApp, b);

        Assert.Equal([HotkeyAction.ToggleScreen, HotkeyAction.ShowApp], book.HeldActions().ToArray());
        Assert.True(book.TryGet(HotkeyAction.ToggleScreen, out var got));
        Assert.Equal(a, got);

        book.Set(HotkeyAction.ToggleScreen, b with { Key = HotkeyKey.C }); // replacing keeps one entry
        Assert.Equal(2, book.HeldActions().Count());
    }

    [Fact]
    public unsafe void NativeStructs_MatchWinuserLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 80 : 48, sizeof(WndClassExW));
        Assert.Equal(8, (int)Marshal.OffsetOf<WndClassExW>(nameof(WndClassExW.WindowProc)));
        Assert.Equal(IntPtr.Size == 8 ? 64 : 40, (int)Marshal.OffsetOf<WndClassExW>(nameof(WndClassExW.ClassName)));
        Assert.Equal(IntPtr.Size == 8 ? 48 : 28, sizeof(Msg));
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, (int)Marshal.OffsetOf<Msg>(nameof(Msg.Message)));
        Assert.Equal(IntPtr.Size == 8 ? 32 : 16, (int)Marshal.OffsetOf<Msg>(nameof(Msg.Time)));
        Assert.Equal(IntPtr.Size == 8 ? 36 : 20, (int)Marshal.OffsetOf<Msg>(nameof(Msg.PointX)));
    }

    [Fact]
    public void Constants_MatchWinuserHeader()
    {
        Assert.Equal(0x0312u, InputConstants.WmHotkey);
        Assert.Equal(0x0002u, InputConstants.WmDestroy);
        Assert.Equal(0x8000u, InputConstants.WmApp);
        Assert.Equal(-3, InputConstants.HwndMessage);
        Assert.Equal(-21, InputConstants.GwlpUserData);
        Assert.Equal(0x4000u, InputConstants.ModNoRepeat);
        Assert.Equal(1409, InputConstants.ErrorHotkeyAlreadyRegistered);
        Assert.Equal(1410, InputConstants.ErrorClassAlreadyExists);
    }
}
