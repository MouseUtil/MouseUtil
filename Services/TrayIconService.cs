using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Manual Shell_NotifyIcon-based system tray icon (no NuGet dependency) - shows/hides a tray icon for
/// the "Close to system tray" feature (see MainWindow.AppWindow_Closing) and raises
/// <see cref="ShowRequested"/>/<see cref="ExitRequested"/> for its left-click and context-menu
/// "Show MouseUtil"/"Exit" actions. Registers its callback message through
/// <see cref="GlobalHotkeyService"/>'s existing WndProc subclass (see RegisterMessageHandler) rather
/// than installing a second one, following the same pattern SingleInstanceService uses.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const int MenuCommandShow = 1;
    private const int MenuCommandExit = 2;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _isVisible;

    /// <summary>Raised when the user left-clicks the tray icon, or picks "Show MouseUtil" from its context menu.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Raised when the user picks "Exit" from the tray icon's context menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Wires this service up to the main window: loads the icon from Assets\app.ico (relative to the
    /// app's own base directory, since this is an unpackaged, self-contained deployment with no
    /// ms-appx:/// resolution) and registers WM_TRAYICON with <paramref name="messageService"/>'s
    /// WndProc subclass. Must be called once, before the first Show(). Does not itself show the icon -
    /// callers decide when based on the persisted CloseToTray setting.
    /// </summary>
    public void Initialize(IntPtr hwnd, GlobalHotkeyService messageService)
    {
        _hwnd = hwnd;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        var cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);
        _hIcon = NativeMethods.LoadImage(IntPtr.Zero, iconPath, NativeMethods.IMAGE_ICON, cx, cy, NativeMethods.LR_LOADFROMFILE);

        messageService.RegisterMessageHandler(NativeMethods.WM_TRAYICON, OnTrayMessage);
    }

    /// <summary>Adds the tray icon if it isn't already showing. No-ops if the icon image failed to load.</summary>
    public void Show()
    {
        if (_isVisible || _hIcon == IntPtr.Zero)
        {
            return;
        }

        var data = BuildIconData();
        _isVisible = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    /// <summary>Removes the tray icon if it's currently showing.</summary>
    public void Hide()
    {
        if (!_isVisible)
        {
            return;
        }

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _isVisible = false;
    }

    private NativeMethods.NOTIFYICONDATA BuildIconData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
        uCallbackMessage = NativeMethods.WM_TRAYICON,
        hIcon = _hIcon,
        szTip = "MouseUtil"
    };

    /// <summary>
    /// Fires on the UI thread (WM_TRAYICON arrives via GlobalHotkeyService's subclassed WndProc, which
    /// already runs on it). lParam carries the actual mouse message: a left-click restores the window
    /// directly, a right-click (or the keyboard-invoked WM_CONTEXTMENU) shows the popup menu instead.
    /// </summary>
    private void OnTrayMessage(IntPtr wParam, IntPtr lParam)
    {
        var mouseMessage = (uint)lParam.ToInt64();

        if (mouseMessage == NativeMethods.WM_LBUTTONUP)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (mouseMessage == NativeMethods.WM_RBUTTONUP || mouseMessage == NativeMethods.WM_CONTEXTMENU)
        {
            ShowContextMenu();
        }
    }

    /// <summary>
    /// Builds and shows the right-click popup menu at the current cursor position, blocking (this runs
    /// synchronously on the UI thread, same as any Win32 tray icon's context menu) until the user picks
    /// an item or dismisses it. SetForegroundWindow before, and the WM_NULL nudge after, are the
    /// standard documented workaround for TrackPopupMenu otherwise failing to dismiss itself when the
    /// user clicks away while this process isn't already the foreground app.
    /// </summary>
    private void ShowContextMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)MenuCommandShow, "Show MouseUtil");
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, (IntPtr)MenuCommandExit, "Exit");

            var cursor = NativeMethods.GetCursorPosition();

            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                cursor.X,
                cursor.Y,
                _hwnd,
                IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuCommandShow)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == MenuCommandExit)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        Hide();

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
