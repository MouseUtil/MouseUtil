using System.Runtime.InteropServices;
using Microsoft.Win32;
using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Manual Shell_NotifyIcon-based system tray icon (no NuGet dependency) - shows/hides a tray icon for
/// the "Close to system tray" feature (see MainWindow.AppWindow_Closing) and raises
/// <see cref="ShowRequested"/>/<see cref="ExitRequested"/> for its left-click and context-menu
/// "Show MouseUtil"/"Exit" actions, plus <see cref="StartRequested"/>/<see cref="StopRequested"/>/
/// <see cref="TogglePauseOnMovementRequested"/> for the context menu's Start/Stop/pause-on-movement
/// items (see ShowContextMenu). Registers its callback message through <see cref="GlobalHotkeyService"/>'s
/// existing WndProc subclass (see RegisterMessageHandler) rather than installing a second one, following
/// the same pattern SingleInstanceService uses.
///
/// Also swaps which icon is displayed to reflect the automation's running/paused state, and - while
/// inactive - the system taskbar's current light/dark theme, via NIM_MODIFY (see UpdateState/
/// UpdateSystemTheme). "Paused" here means MouseAutomationEngine.StatusKind.Paused (Spin mode's
/// pause-on-movement countdown), unrelated to AutomationMode.Spin itself. The same NIM_MODIFY call also
/// keeps the tooltip (szTip) in sync with a live "MouseUtil: {mode} - {Active/Paused/Inactive}" string
/// (see BuildTooltipText).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const int MenuCommandShow = 1;
    private const int MenuCommandExit = 2;
    private const int MenuCommandStartClick = 3;
    private const int MenuCommandStartSpin = 4;
    private const int MenuCommandStop = 5;
    private const int MenuCommandTogglePauseOnMovement = 6;

    private IntPtr _hwnd;
    private bool _isVisible;

    // Preloaded once at Initialize() rather than re-loaded from disk on every state change.
    private IntPtr _hIconActive;
    private IntPtr _hIconPaused;
    private IntPtr _hIconInactiveLightTheme;
    private IntPtr _hIconInactiveDarkTheme;

    // Current composite state, tracked so (1) NIM_MODIFY is only called when the resulting icon/tooltip
    // actually changed, (2) Show() - after the icon was hidden via CloseToTray/NIM_DELETE - can re-add
    // it with whatever icon/tooltip was last correct instead of defaulting back to a stale one, and (3)
    // the context menu's Start/Stop item enabling reflects live state without MainWindow having to push
    // it separately (see ShowContextMenu).
    private bool _isRunning;
    private bool _isPaused;
    private bool _isTaskbarLight;
    private AutomationMode _mode = AutomationMode.Click;
    private IntPtr _currentHIcon;
    private string _currentTip = "MouseUtil";

    /// <summary>Raised when the user left-clicks the tray icon, or picks "Show MouseUtil" from its context menu.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Raised when the user picks "Exit" from the tray icon's context menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user picks "Start Auto Click"/"Start Spin Mode" from the context menu (only reachable while inactive - see ShowContextMenu).</summary>
    public event EventHandler<AutomationMode>? StartRequested;

    /// <summary>Raised when the user picks "Stop" from the context menu (only reachable while running - see ShowContextMenu).</summary>
    public event EventHandler? StopRequested;

    /// <summary>Raised when the user picks "Pause spinning on movement" from the context menu (only reachable while inactive - see ShowContextMenu).</summary>
    public event EventHandler? TogglePauseOnMovementRequested;

    /// <summary>
    /// Wires this service up to the main window: preloads all four tray icon variants from Assets\
    /// (relative to the app's own base directory, since this is an unpackaged, self-contained
    /// deployment with no ms-appx:/// resolution), reads the current system taskbar theme from the
    /// registry, and registers WM_TRAYICON/WM_SETTINGCHANGE with <paramref name="messageService"/>'s
    /// WndProc subclass. Must be called once, before the first Show(). Does not itself show the icon -
    /// callers decide when based on the persisted CloseToTray setting.
    /// </summary>
    public void Initialize(IntPtr hwnd, GlobalHotkeyService messageService)
    {
        _hwnd = hwnd;

        var cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        var cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);
        _hIconActive = LoadIcon("app.ico", cx, cy);
        _hIconPaused = LoadIcon("tray-paused.ico", cx, cy);
        _hIconInactiveLightTheme = LoadIcon("tray-inactive-light.ico", cx, cy);
        _hIconInactiveDarkTheme = LoadIcon("tray-inactive-dark.ico", cx, cy);

        _isTaskbarLight = ReadIsTaskbarLightFromRegistry();
        _currentHIcon = GetIconForCurrentState();
        _currentTip = BuildTooltipText();

        messageService.RegisterMessageHandler(NativeMethods.WM_TRAYICON, OnTrayMessage);
        messageService.RegisterMessageHandler(NativeMethods.WM_SETTINGCHANGE, OnSettingChange);
    }

    private static IntPtr LoadIcon(string fileName, int cx, int cy)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        return NativeMethods.LoadImage(IntPtr.Zero, path, NativeMethods.IMAGE_ICON, cx, cy, NativeMethods.LR_LOADFROMFILE);
    }

    /// <summary>
    /// Updates the running/paused/mode portion of the composite tray icon+tooltip state - called from
    /// PowerToggleButton_Checked/_Unchecked (running flips), Engine_StatusChanged/ApplyEngineStatus
    /// (paused flips while running), and MainWindow's mode-switch path (mode flips while inactive -
    /// mode never changes while running). Applies NIM_MODIFY immediately if the icon is currently
    /// visible and the resulting icon or tooltip actually changed.
    /// </summary>
    public void UpdateState(bool isRunning, bool isPaused, AutomationMode mode)
    {
        _isRunning = isRunning;
        _isPaused = isRunning && isPaused;
        _mode = mode;
        ApplyStateIfChanged();
    }

    /// <summary>
    /// Updates the system taskbar theme portion of the composite state (see the registry read in
    /// Initialize/OnSettingChange). Only re-evaluates the displayed icon while Inactive - the
    /// Active/Paused icons (app.ico/tray-paused.ico) don't have theme variants.
    /// </summary>
    public void UpdateSystemTheme(bool isTaskbarLight)
    {
        _isTaskbarLight = isTaskbarLight;
        if (!_isRunning)
        {
            ApplyStateIfChanged();
        }
    }

    private IntPtr GetIconForCurrentState()
    {
        if (_isPaused)
        {
            return _hIconPaused;
        }

        if (_isRunning)
        {
            return _hIconActive;
        }

        return _isTaskbarLight ? _hIconInactiveLightTheme : _hIconInactiveDarkTheme;
    }

    /// <summary>
    /// "MouseUtil: {ModeDisplayName} - {Active/Paused/Inactive}", e.g. "MouseUtil: Auto click -
    /// Inactive" or "MouseUtil: Spin mode - Active". Mode display names match ModeSubtitleTextBlock.Text
    /// in MainWindow (see UpdateModeIndicators) so the tray tooltip and the main window agree on wording.
    /// </summary>
    private string BuildTooltipText()
    {
        var modeName = _mode == AutomationMode.Spin ? "Spin mode" : "Auto click";
        var statusWord = !_isRunning ? "Inactive" : _isPaused ? "Paused" : "Active";
        return $"MouseUtil: {modeName} - {statusWord}";
    }

    private void ApplyStateIfChanged()
    {
        var icon = GetIconForCurrentState();
        var tip = BuildTooltipText();
        if (icon == _currentHIcon && tip == _currentTip)
        {
            return;
        }

        _currentHIcon = icon;
        _currentTip = tip;

        if (!_isVisible)
        {
            // Nothing to modify while hidden - Show() below picks up _currentHIcon/_currentTip when
            // re-added.
            return;
        }

        var data = BuildIconData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>Adds the tray icon if it isn't already showing. No-ops if the icon image failed to load.</summary>
    public void Show()
    {
        if (_isVisible || _currentHIcon == IntPtr.Zero)
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
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _isVisible = false;
    }

    private NativeMethods.NOTIFYICONDATA BuildIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
        uCallbackMessage = NativeMethods.WM_TRAYICON,
        hIcon = _currentHIcon,
        szTip = _currentTip
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
    /// Fires on the UI thread (WM_SETTINGCHANGE arrives via the same subclassed WndProc). lParam is a
    /// pointer to the name of the setting that changed; Windows broadcasts "ImmersiveColorSet"
    /// specifically for the light/dark theme toggle in Settings > Personalization > Colors.
    /// </summary>
    private void OnSettingChange(IntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero || Marshal.PtrToStringUni(lParam) != "ImmersiveColorSet")
        {
            return;
        }

        UpdateSystemTheme(ReadIsTaskbarLightFromRegistry());
    }

    /// <summary>
    /// Reads the taskbar/tray theme (not the app's own theme preference) from the registry. Defaults
    /// to dark-taskbar behavior (white icon) if the key is missing/unreadable.
    /// </summary>
    private static bool ReadIsTaskbarLightFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds and shows the right-click popup menu at the current cursor position, blocking (this runs
    /// synchronously on the UI thread, same as any Win32 tray icon's context menu) until the user picks
    /// an item or dismisses it. SetForegroundWindow before, and the WM_NULL nudge after, are the
    /// standard documented workaround for TrackPopupMenu otherwise failing to dismiss itself when the
    /// user clicks away while this process isn't already the foreground app.
    ///
    /// Rebuilt from scratch every time it's shown, so "Start Auto Click"/"Start Spin Mode"/"Stop"
    /// enabled-vs-grayed and "Pause spinning on movement" checked/grayed always reflect state as of
    /// this exact click - _isRunning (kept current by UpdateState) and a fresh ConfigService.Load()
    /// read (the actual source of truth PauseOnMovementToggle itself writes through - see
    /// MainWindow.PauseOnMovementToggle_Toggled) rather than anything cached from an earlier show.
    /// Start/Stop are the simple "enabled only in the applicable running state" rule described in the
    /// feature spec - not a live mode-switch affordance, so which mode happens to be selected in the
    /// main window UI doesn't affect either Start item's enabled state.
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
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);

            var startFlags = NativeMethods.MF_STRING | (_isRunning ? NativeMethods.MF_GRAYED : NativeMethods.MF_ENABLED);
            NativeMethods.AppendMenu(hMenu, startFlags, (IntPtr)MenuCommandStartClick, "Start Auto Click");
            NativeMethods.AppendMenu(hMenu, startFlags, (IntPtr)MenuCommandStartSpin, "Start Spin Mode");

            var stopFlags = NativeMethods.MF_STRING | (_isRunning ? NativeMethods.MF_ENABLED : NativeMethods.MF_GRAYED);
            NativeMethods.AppendMenu(hMenu, stopFlags, (IntPtr)MenuCommandStop, "Stop");

            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);

            var pauseOnMovement = ConfigService.Load().PauseOnMovement;
            var pauseFlags = NativeMethods.MF_STRING
                | (pauseOnMovement ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED)
                | (_isRunning ? NativeMethods.MF_GRAYED : NativeMethods.MF_ENABLED);
            NativeMethods.AppendMenu(hMenu, pauseFlags, (IntPtr)MenuCommandTogglePauseOnMovement, "Pause spinning on movement");

            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, string.Empty);
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
            else if (command == MenuCommandStartClick)
            {
                StartRequested?.Invoke(this, AutomationMode.Click);
            }
            else if (command == MenuCommandStartSpin)
            {
                StartRequested?.Invoke(this, AutomationMode.Spin);
            }
            else if (command == MenuCommandStop)
            {
                StopRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (command == MenuCommandTogglePauseOnMovement)
            {
                TogglePauseOnMovementRequested?.Invoke(this, EventArgs.Empty);
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

        DestroyIconIfLoaded(ref _hIconActive);
        DestroyIconIfLoaded(ref _hIconPaused);
        DestroyIconIfLoaded(ref _hIconInactiveLightTheme);
        DestroyIconIfLoaded(ref _hIconInactiveDarkTheme);
        _currentHIcon = IntPtr.Zero;
    }

    private static void DestroyIconIfLoaded(ref IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(hIcon);
            hIcon = IntPtr.Zero;
        }
    }
}
