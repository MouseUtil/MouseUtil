namespace MouseUtil.Models;

public sealed class AppConfig
{
    public double IntervalMinutes { get; set; } = 1;
    public double IntervalSeconds { get; set; } = 0;
    public string Theme { get; set; } = "System";
    public bool PauseOnMovement { get; set; } = true;
    public string LastMode { get; set; } = "Click";

    // Default ON: shows a running click/spin count on the power button (while running, on hover-out)
    // and a "Stopped after N clicks/spins" summary when automation stops normally. See
    // MainWindow.UpdatePowerButtonRunningDisplay / PowerToggleButton_Unchecked.
    public bool ShowActionCounter { get; set; } = true;

    // Global Start/Stop hotkey, persisted as the raw RegisterHotKey bitmask/virtual-key pair rather
    // than a formatted string, so re-registering on startup (see MainWindow's GlobalHotkeyService
    // setup) never needs to re-parse a display string. Default is a bare F6 (HotkeyModifiers = 0,
    // i.e. no Ctrl/Alt/Shift/Win required) - RegisterHotKey allows zero modifiers for keys like
    // function keys that aren't commonly bound elsewhere.
    public uint HotkeyModifiers { get; set; } = 0;
    public uint HotkeyKey { get; set; } = 0x75; // VK_F6

    // Which Auto Stop mode (if any) is currently configured - "None" (never configured / feature
    // untouched), "Count" (stop after AutoStopCount clicks/spins), or "DateTime" (stop at a specific
    // date+time). Unlike the date+time value itself (deliberately session-only, see MainWindow's
    // _stopDateTime), the mode and count ARE persisted: a "stop after N clicks" configuration stays
    // meaningful indefinitely, unlike a specific calendar date/time.
    public string AutoStopMode { get; set; } = "None";
    public int AutoStopCount { get; set; } = 100;

    // Default OFF: when true, closing the main window (X button, Alt+F4, taskbar close) hides it to
    // the system tray instead of exiting the app - see MainWindow.AppWindow_Closing and
    // Services/TrayIconService. The app keeps running (automation, if any, is unaffected); the tray
    // icon's "Exit" command is the only way to actually terminate the process while this is on.
    public bool CloseToTray { get; set; } = false;

    // Default OFF: when true, the running interval countdown is mirrored as a progress bar overlaid
    // on the app's taskbar icon (via ITaskbarList3 - see Services/TaskbarProgressService), the same
    // mechanism installers use. Turns amber/yellow automatically while paused (pause-on-movement).
    public bool ShowTaskbarProgress { get; set; } = false;
}
