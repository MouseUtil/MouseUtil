using System.Runtime.InteropServices;

namespace MouseUtil.Interop;

/// <summary>
/// All Win32 P/Invoke declarations for cursor control live here. WinUI 3 apps can P/Invoke
/// user32.dll directly for real cursor positioning/clicking instead of a higher-level automation API.
/// </summary>
internal static class NativeMethods
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    // Global hotkey support (RegisterHotKey/WM_HOTKEY) - see Services/GlobalHotkeyService, which
    // subclasses the main window's WndProc to observe WM_HOTKEY messages Windows delivers to
    // whichever thread registered the hotkey, regardless of which window/app currently has focus.
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    // Suppresses OS auto-repeat WM_HOTKEY messages while the key combination is held down - without
    // this, holding the hotkey would fire repeated Start/Stop toggles instead of exactly one.
    public const uint MOD_NOREPEAT = 0x4000;

    public const int GWLP_WNDPROC = -4;

    // Single-instance enforcement (see Services/SingleInstanceService and Services/GlobalHotkeyService)
    // - RegisterWindowMessage mints a message id that every process registering the same string
    // resolves to identically, so a second launch attempt can signal the first without any other IPC;
    // FindWindow locates the first instance's window by its fixed title; PostMessage delivers the
    // signal; SetForegroundWindow/ShowWindow bring it to the front, restoring it first if minimized.
    public const int SW_RESTORE = 9;

    // System tray icon support (Shell_NotifyIcon) - see Services/TrayIconService, which registers
    // WM_TRAYICON as a callback message through GlobalHotkeyService's existing WndProc subclass rather
    // than installing its own. lParam of that callback message is one of the mouse message ids below
    // (the icon's default, pre-NIM_SETVERSION behavior), telling us which mouse action occurred.
    public const uint WM_TRAYICON = WM_APP + 1;
    public const uint WM_APP = 0x8000;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_CONTEXTMENU = 0x007B;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    public const uint MF_STRING = 0x00000000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public struct Point
    {
        public int X;
        public int Y;
    }

    // Mirrors the Win32 NOTIFYICONDATAW struct exactly (field order/sizes matter - this is passed by
    // ref straight into shell32.dll). Only the fields TrayIconService actually sets (hWnd/uID/uFlags/
    // uCallbackMessage/hIcon/szTip) are meaningful here; the rest exist purely so the struct's layout
    // and cbSize match what Shell_NotifyIcon expects.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // The flat "SetWindowLongPtr"/"CallWindowProc" names are C-header macros, not real exports -
    // the actual exported symbols are always the wide (W) variants, so EntryPoint must say so
    // explicitly; DllImport would otherwise look for a literally-named export that doesn't exist.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Real exported symbols are RegisterWindowMessageW/FindWindowW/PostMessageW (the ANSI/Unicode
    // pair, not macros like SetWindowLongPtr above) - EntryPoint pins each to its wide variant
    // explicitly, and CharSet=Unicode ensures the string parameters marshal to match it.
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsIconic(IntPtr hWnd);

    // Tray icon support (see Services/TrayIconService).
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    public static Point GetCursorPosition()
    {
        GetCursorPos(out var p);
        return new Point { X = p.X, Y = p.Y };
    }

    /// <summary>
    /// Positions the cursor directly, then fires a genuine zero-delta synthesized input event.
    /// SetCursorPos alone does not reset Windows' idle/sleep timer - only a real SendInput event does.
    /// </summary>
    public static void MoveCursorTo(int x, int y)
    {
        SetCursorPos(x, y);
        SendZeroDeltaMove();
    }

    public static void SendZeroDeltaMove()
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void SendLeftClick()
    {
        var down = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        var up = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }
}
