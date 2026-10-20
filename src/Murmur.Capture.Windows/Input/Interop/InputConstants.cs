namespace Murmur.Capture.Windows.Input.Interop;

/// <summary>Win32 constants of the global-hotkey code (winuser.h, winerror.h; Windows SDK 10.0.26100).</summary>
internal static class InputConstants
{
    // RegisterHotKey fsModifiers
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    // Window messages
    public const uint WmDestroy = 0x0002;
    public const uint WmHotkey = 0x0312;
    public const uint WmApp = 0x8000;

    /// <summary>Private: a Register request is waiting in the request slot.</summary>
    public const uint WmRegisterRequest = WmApp + 1;

    /// <summary>Private: unregister everything, destroy the window and end the message loop.</summary>
    public const uint WmShutdown = WmApp + 2;

    /// <summary><c>HWND_MESSAGE</c>: the parent of message-only windows.</summary>
    public static readonly nint HwndMessage = -3;

    /// <summary><c>GWLP_USERDATA</c>.</summary>
    public const int GwlpUserData = -21;

    // winerror.h
    public const int ErrorClassAlreadyExists = 1410;
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    // SendInput / virtual keys used by the tests and the key table
    public const ushort VkPause = 0x13;
    public const ushort VkSpace = 0x20;
    public const ushort VkPrior = 0x21;
    public const ushort VkNext = 0x22;
    public const ushort VkEnd = 0x23;
    public const ushort VkHome = 0x24;
    public const ushort VkLeft = 0x25;
    public const ushort VkUp = 0x26;
    public const ushort VkRight = 0x27;
    public const ushort VkDown = 0x28;
    public const ushort VkSnapshot = 0x2C;
    public const ushort VkInsert = 0x2D;
    public const ushort VkDelete = 0x2E;
    public const ushort VkNumpad0 = 0x60;
    public const ushort VkF1 = 0x70;
    public const ushort VkOem1 = 0xBA;
    public const ushort VkOemPlus = 0xBB;
    public const ushort VkOemComma = 0xBC;
    public const ushort VkOemMinus = 0xBD;
    public const ushort VkOemPeriod = 0xBE;
    public const ushort VkOem2 = 0xBF;
    public const ushort VkOem3 = 0xC0;
    public const ushort VkOem4 = 0xDB;
    public const ushort VkOem5 = 0xDC;
    public const ushort VkOem6 = 0xDD;
    public const ushort VkOem7 = 0xDE;
    public const ushort VkOem8 = 0xDF;
    public const ushort VkOem102 = 0xE2;
}
