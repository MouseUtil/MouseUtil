namespace MouseUtil.Models;

public sealed class AppConfig
{
    public double IntervalMinutes { get; set; } = 1;
    public double IntervalSeconds { get; set; } = 0;
    public string Theme { get; set; } = "System";
    public bool PauseOnMovement { get; set; } = true;

    // Per-mode scoping for PauseOnMovement above (which stays the master on/off switch - both of these
    // are only meaningful while it's on). PauseOnMovementForJiggle defaults ON to preserve the
    // feature's original, pre-scoping behavior (pause-on-movement always applied to Jiggle mode and only
    // Jiggle mode); PauseOnMovementForAutoClick defaults OFF since letting it apply to Auto click mode is
    // new, not a carried-over default. See SettingsPanel's PauseOnMovementExpander ("Auto click"/"Jiggle"
    // sub-toggles) and IsPauseOnMovementActiveForMode, which combines all three into the single
    // bool MouseAutomationEngine.Start actually receives.
    public bool PauseOnMovementForAutoClick { get; set; } = false;
    public bool PauseOnMovementForJiggle { get; set; } = true;

    public string LastMode { get; set; } = "Click";

    // Which mode PowerToggleButton uses while ShowStopButtonDisplay is on: "Counter" (shows a running
    // click/jiggle count, and a "Stopped after N clicks/jiggles" summary when automation stops normally)
    // or "Countdown" (default - shows time remaining until the next click/jiggle, with click/jiggle
    // counts and pause/resume state moved to the status bar instead). See
    // MainWindow.UpdatePowerButtonRunningDisplay / UpdatePowerButtonCountdownDisplay /
    // PowerToggleButton_Unchecked.
    public string StopButtonDisplayMode { get; set; } = "Countdown";

    // Default ON: whether PowerToggleButton shows anything beyond plain "Stop" while running at all -
    // when off, the button always shows plain "Stop" (never a count or countdown or the separate
    // paused element), and both the countdown-until-next-action and the running click/jiggle count move
    // to the status bar instead, combined on one line. Gates "Stop button display"'s dropdown
    // (StopButtonDisplayMode above) in Settings - see SettingsPanel's ShowStopButtonDisplayToggle and
    // MainWindow.UpdatePowerButtonNoneDisplay.
    public bool ShowStopButtonDisplay { get; set; } = true;

    // Global Start/Stop hotkey, persisted as the raw RegisterHotKey bitmask/virtual-key pair rather
    // than a formatted string, so re-registering on startup (see MainWindow's GlobalHotkeyService
    // setup) never needs to re-parse a display string. Default is a bare F6 (HotkeyModifiers = 0,
    // i.e. no Ctrl/Alt/Shift/Win required) - RegisterHotKey allows zero modifiers for keys like
    // function keys that aren't commonly bound elsewhere.
    public uint HotkeyModifiers { get; set; } = 0;
    public uint HotkeyKey { get; set; } = 0x75; // VK_F6

    // Which Auto Stop mode (if any) is currently configured - "None" (never configured / feature
    // untouched), "Count" (stop after AutoStopCount clicks/jiggles), or "DateTime" (stop at a specific
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

    // Default OFF: whether Advanced interval display mode (the Hours/Minutes/Seconds/Milliseconds
    // fields, replacing the plain Minutes/Seconds ones) is currently active - unlike
    // _isRandomizeIntervalEnabled (which is deliberately never persisted, always starting Off), this
    // one IS persisted, and IS the actual source of truth restored into
    // _isAdvancedIntervalDisplayEnabled at startup (see MainWindow.LoadConfigIntoUi/
    // UpdateAdvancedIntervalDisplayMode). Owned and edited exclusively by SettingsPanel's "Interval
    // display" dropdown - there used to also be an AdvancedIntervalDisplayButton
    // directly in the Interval card for this, but it was removed entirely (not just hidden) in favor
    // of controlling this from Settings only.
    public bool ShowAdvancedIntervalDisplay { get; set; } = false;

    // Default OFF: whether automation (Auto click/Jiggle, per PreferredMode below) should start
    // immediately every time the app launches - regardless of whether this particular launch came
    // from Windows startup (see StartupTaskService/StartWithWindowsCard's own comment) or a normal
    // manual launch. See SettingsPanel's "App launch behavior" expander and MainWindow's constructor.
    public bool RunAutomationOnLaunch { get; set; } = false;

    // Which mode is selected at launch: "LastUsed" (default - restores LastMode above, today's
    // existing behavior) or a forced "Click"/"Jiggle" regardless of what LastMode says. Independent
    // of RunAutomationOnLaunch above - this decides which mode the UI shows even when automation
    // isn't also starting automatically. See SettingsPanel's "Preferred mode" dropdown and
    // MainWindow.LoadConfigIntoUi.
    public string PreferredMode { get; set; } = "LastUsed";

    // Default OFF: whether the Interval card's "Randomize interval" toggle should start already On at
    // launch. Unlike _isRandomizeIntervalEnabled itself (MainWindow's own field - deliberately never
    // persisted, always starting Off each session), this setting IS persisted, since it's an explicit
    // opt-in the user configures once here rather than the button's own always-resets-Off default. See
    // SettingsPanel's "Randomize interval when app launches" toggle and MainWindow's constructor.
    public bool RandomizeIntervalOnLaunch { get; set; } = false;
}
