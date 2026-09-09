using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MouseUtil.Interop;
using MouseUtil.Services;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Core;

namespace MouseUtil.Controls;

/// <summary>Which of PowerToggleButton's two live-running displays is active while ShowStopButtonDisplayToggle is on - see SettingsPanel's "Stop button display" dropdown and MainWindow.UpdatePowerButtonRunningDisplay.</summary>
public enum StopButtonDisplayMode
{
    Counter,
    Countdown
}

/// <summary>
/// The settings rows shown in MainWindow's Settings overlay (see MainWindow.xaml's SettingsOverlay
/// and ShowSettingsOverlay/SettingsBackButton_Click) - a single instance with exactly one
/// AutomationId per control, hosted in exactly one place for its whole lifetime.
///
/// Everything window/system-level stays in MainWindow (GlobalHotkeyService's actual RegisterHotKey
/// calls, RootGrid.RequestedTheme, TrayIconService, TaskbarProgressService) - this control only owns
/// the rows' own state/persistence and exposes events/delegates for the handful of things that still
/// need to reach MainWindow. See each event's doc comment below for which MainWindow-side effect it
/// drives.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    /// <summary>Raised (guarded by _isInitializing) whenever the Theme selection changes, so MainWindow can run ApplyTheme.</summary>
    public event EventHandler<string>? ThemeSelectionChanged;

    /// <summary>Raised whenever "Keep running in system tray" changes, so MainWindow can refresh its own _closeToTray mirror and UpdateTrayIconVisibility.</summary>
    public event EventHandler? CloseToTrayChanged;

    /// <summary>Raised whenever "Show countdown progress on taskbar icon" changes, so MainWindow can refresh its own _showTaskbarProgress mirror and clear the taskbar progress bar if it was just turned off.</summary>
    public event EventHandler? ShowTaskbarProgressChanged;

    /// <summary>Raised whenever "Pause on movement" changes - the master toggle, or either "Auto click"/"Jiggle" sub-toggle (by the user or via TogglePauseOnMovement) - so MainWindow can call ResetStatusToOffIfNotRunning.</summary>
    public event EventHandler? PauseOnMovementChanged;

    /// <summary>Raised whenever "Stop button display" changes, so MainWindow can refresh PowerToggleButton's live display.</summary>
    public event EventHandler? StopButtonDisplayChanged;

    /// <summary>Raised whenever "Interval display" changes, so MainWindow can swap BasicIntervalRow/AdvancedIntervalRow to match (see MainWindow.UpdateAdvancedIntervalDisplayMode).</summary>
    public event EventHandler? ShowAdvancedIntervalDisplayChanged;

    /// <summary>
    /// MainWindow supplies this so hotkey recording can still go through its GlobalHotkeyService
    /// (the actual RegisterHotKey call requires the WndProc subclass installed on the window itself,
    /// which can't move into this UserControl). Returns whether registration succeeded, exactly like
    /// GlobalHotkeyService.TryRegister.
    /// </summary>
    public Func<uint, uint, bool>? TryRegisterHotkey { get; set; }

    /// <summary>
    /// MainWindow supplies this alongside TryRegisterHotkey so recording can unregister the current
    /// hotkey for the duration of the capture - see HotkeyButton_Click's doc comment for why.
    /// </summary>
    public Action? UnregisterHotkey { get; set; }

    public StopButtonDisplayMode StopButtonDisplay =>
        StopButtonDisplayCounterRadioButton.IsChecked == true
            ? StopButtonDisplayMode.Counter
            : StopButtonDisplayMode.Countdown;

    /// <summary>Whether PowerToggleButton shows anything beyond plain "Stop" while running at all - see AppConfig.ShowStopButtonDisplay's own comment.</summary>
    public bool IsStopButtonDisplayShown => ShowStopButtonDisplayToggle.IsOn;

    public bool CloseToTray => CloseToTrayToggle.IsOn;
    public bool ShowTaskbarProgress => ShowTaskbarProgressToggle.IsOn;

    public bool ShowAdvancedIntervalDisplay => IntervalDisplayAdvancedRadioButton.IsChecked == true;

    public bool RunAutomationOnLaunch => RunAutomationOnLaunchToggle.IsOn;
    public bool RandomizeIntervalOnLaunch => RandomizeIntervalOnLaunchToggle.IsOn;

    /// <summary>"LastUsed", "Click", or "Jiggle" - see AppConfig.PreferredMode's own comment. Backed by a plain field (rather than read back from PreferredModeDropDownButton) since DropDownButton/MenuFlyoutItem have no built-in "currently selected item" concept the way ComboBox does.</summary>
    public string PreferredMode => _preferredMode;

    private bool _isInitializing;
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _isRecordingHotkey;
    private string _preferredMode = "LastUsed";

    public SettingsPanel()
    {
        InitializeComponent();

        LoadFromConfig();
        _ = LoadStartupTaskStateAsync();
    }

    private void LoadFromConfig()
    {
        _isInitializing = true;

        var config = ConfigService.Load();

        PauseOnMovementToggle.IsOn = config.PauseOnMovement;
        PauseOnMovementAutoClickToggle.IsOn = config.PauseOnMovementForAutoClick;
        PauseOnMovementJiggleToggle.IsOn = config.PauseOnMovementForJiggle;
        PauseOnMovementAutoClickToggle.IsEnabled = config.PauseOnMovement;
        PauseOnMovementJiggleToggle.IsEnabled = config.PauseOnMovement;
        UpdatePauseOnMovementExpanderDescription();

        ShowStopButtonDisplayToggle.IsOn = config.ShowStopButtonDisplay;
        StopButtonDisplayCountdownRadioButton.IsEnabled = config.ShowStopButtonDisplay;
        StopButtonDisplayCounterRadioButton.IsEnabled = config.ShowStopButtonDisplay;
        (config.StopButtonDisplayMode switch
        {
            "Counter" => StopButtonDisplayCounterRadioButton,
            _ => StopButtonDisplayCountdownRadioButton
        }).IsChecked = true;
        UpdateStopButtonDisplayExpanderDescription();

        CloseToTrayToggle.IsOn = config.CloseToTray;
        ShowTaskbarProgressToggle.IsOn = config.ShowTaskbarProgress;

        RunAutomationOnLaunchToggle.IsOn = config.RunAutomationOnLaunch;
        RandomizeIntervalOnLaunchToggle.IsOn = config.RandomizeIntervalOnLaunch;

        _preferredMode = config.PreferredMode;
        PreferredModeDropDownButton.Content = FormatPreferredMode(_preferredMode);

        (config.ShowAdvancedIntervalDisplay ? IntervalDisplayAdvancedRadioButton : IntervalDisplayBasicRadioButton).IsChecked = true;
        UpdateIntervalDisplayExpanderDescription();

        _hotkeyModifiers = config.HotkeyModifiers;
        _hotkeyKey = config.HotkeyKey;
        HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);

        (config.Theme switch
        {
            "Light" => ThemeLightRadioButton,
            "Dark" => ThemeDarkRadioButton,
            _ => ThemeSystemRadioButton
        }).IsChecked = true;
        UpdateThemeExpanderDescription();

        _isInitializing = false;
    }

    /// <summary>
    /// Queries the OS's current StartupTask state and reflects it onto StartWithWindowsToggle. Runs
    /// fire-and-forget from the constructor since StartupTask.GetAsync is a WinRT async call, unlike
    /// the synchronous ConfigService.Load() that LoadFromConfig uses.
    /// </summary>
    private async Task LoadStartupTaskStateAsync()
    {
        StartupTaskState state;
        try
        {
            state = await StartupTaskService.GetStateAsync();
        }
        catch (Exception)
        {
            // Not running packaged/registered (e.g. launched as a raw .exe rather than via the Start
            // Menu/winapp run) - StartupTask.GetAsync throws in that case. Hide the row rather than
            // showing a toggle that can never actually do anything.
            StartWithWindowsCard.Visibility = Visibility.Collapsed;
            return;
        }

        _isInitializing = true;
        StartWithWindowsToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        StartWithWindowsToggle.IsEnabled = state is not (StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy);
        _isInitializing = false;
    }

    /// <summary>
    /// Also drives ThemeExpanderIcon's Glyph - System keeps the expander's own XAML-declared
    /// default ("Color") icon; Light/Dark switch to the same per-mode sun/moon icons
    /// ThemeLightItem/ThemeDarkItem used before Theme's options became plain RadioButtons.
    /// </summary>
    private void UpdateThemeExpanderDescription()
    {
        if (ThemeLightRadioButton.IsChecked == true)
        {
            ThemeExpander.Description = "Light";
            ThemeExpanderIcon.Glyph = "";
        }
        else if (ThemeDarkRadioButton.IsChecked == true)
        {
            ThemeExpander.Description = "Dark";
            ThemeExpanderIcon.Glyph = "";
        }
        else
        {
            ThemeExpander.Description = "Follow system";
            ThemeExpanderIcon.Glyph = "";
        }
    }

    private void UpdateIntervalDisplayExpanderDescription() =>
        IntervalDisplayExpander.Description = IntervalDisplayAdvancedRadioButton.IsChecked == true
            ? "Hours, Minutes, Seconds, Milliseconds"
            : "Minutes and Seconds";

    /// <summary>
    /// Mirrors UpdateStopButtonDisplayExpanderDescription's style for PauseOnMovementExpander: "Off"
    /// while the master toggle is off, otherwise which of "Auto click"/"Jiggle" the two sub-toggles
    /// currently apply to (comma-joined - both, either alone, or "On" if the master is on but neither
    /// sub-toggle is, which is a valid - if inert - combination rather than one this needs to prevent).
    /// </summary>
    private void UpdatePauseOnMovementExpanderDescription()
    {
        if (!PauseOnMovementToggle.IsOn)
        {
            PauseOnMovementExpander.Description = "Off";
            return;
        }

        var modes = new List<string>();
        if (PauseOnMovementAutoClickToggle.IsOn)
        {
            modes.Add("Auto click");
        }

        if (PauseOnMovementJiggleToggle.IsOn)
        {
            modes.Add("Jiggle");
        }

        PauseOnMovementExpander.Description = modes.Count > 0 ? string.Join(", ", modes) : "On";
    }

    private void UpdateStopButtonDisplayExpanderDescription() =>
        StopButtonDisplayExpander.Description = !ShowStopButtonDisplayToggle.IsOn
            ? "Off"
            : StopButtonDisplayCounterRadioButton.IsChecked == true
                ? "Click/jiggle count"
                : "Time remaining";

    /// <summary>
    /// Called immediately by MainWindow's SettingsBackButton_Click when the Settings overlay starts
    /// closing (before the slide-out animation and ResetAfterClose's own delay below) - cancels any
    /// in-progress hotkey capture (otherwise _isRecordingHotkey stays stuck true, permanently
    /// no-opping HotkeyButton_Click) and, since recording leaves the previous hotkey unregistered
    /// (see HotkeyButton_Click), re-registers it so closing Settings mid-capture never leaves the app
    /// with no hotkey active. This can't wait for ResetAfterClose's delay like the expanders/error
    /// banner do - the hotkey has to be re-registered right away, not 300ms into the user having
    /// already left Settings.
    /// </summary>
    public void HandleHostClosing()
    {
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            TryRegisterHotkey?.Invoke(_hotkeyModifiers, _hotkeyKey);
        }
    }

    /// <summary>
    /// Called by MainWindow's SettingsBackButton_Click once the slide-out animation has fully
    /// finished (300ms after HandleHostClosing above, alongside SettingsScrollViewer's own reset back
    /// to the top) - resets everything else Settings should always come back to fresh, however the
    /// user left it: collapses every expandable card, and clears the startup-task error banner (see
    /// StartWithWindowsToggle_Toggled) rather than leaving a stale error sitting there until the app
    /// is relaunched. Deliberately delayed rather than run immediately like HandleHostClosing does,
    /// so none of this is visible mid-slide - the expander collapse also sidesteps a real gap in
    /// SettingsExpander itself (confirmed against the installed
    /// CommunityToolkit.WinUI.Controls.SettingsControls source): disabling an expander while
    /// automation is running only blocks its chevron's clicks, it doesn't collapse anything, so one
    /// left open when automation starts would otherwise stay frozen open with no way for the user to
    /// collapse it mid-run - closing Settings already guarantees a fresh, collapsed state instead.
    /// </summary>
    public void ResetAfterClose()
    {
        ThemeExpander.IsExpanded = false;
        IntervalDisplayExpander.IsExpanded = false;
        StopButtonDisplayExpander.IsExpanded = false;
        PauseOnMovementExpander.IsExpanded = false;
        AppLaunchBehaviorExpander.IsExpanded = false;

        StartupTaskErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    /// <summary>Flips PauseOnMovementToggle (the master switch) - used by MainWindow's tray "Pause on movement" context menu item so PauseOnMovementToggle_Toggled remains the single place that persists/raises the change.</summary>
    public void TogglePauseOnMovement() => PauseOnMovementToggle.IsOn = !PauseOnMovementToggle.IsOn;

    /// <summary>
    /// Same set of controls MainWindow.SetInputsEnabled used to poke directly before these rows
    /// moved into SettingsPanel.
    /// </summary>
    public void SetInputsEnabled(bool enabled)
    {
        HotkeyCard.IsEnabled = enabled;

        // StopButtonDisplayExpander itself (not just its toggle/radio buttons) has to be disabled too,
        // otherwise its chevron stays clickable and the user can still expand/collapse it while
        // everything inside is locked - unlike IntervalDisplayExpander below, whose own IsEnabled was
        // already covering this same case.
        StopButtonDisplayExpander.IsEnabled = enabled;
        ShowStopButtonDisplayToggle.IsEnabled = enabled;
        StopButtonDisplayCountdownRadioButton.IsEnabled = enabled && ShowStopButtonDisplayToggle.IsOn;
        StopButtonDisplayCounterRadioButton.IsEnabled = enabled && ShowStopButtonDisplayToggle.IsOn;

        IntervalDisplayExpander.IsEnabled = enabled;

        // Both the wrapping SettingsExpander (so its own native Disabled visual state dims the Header
        // text/icon, and its chevron can't be clicked mid-run - same reasoning as
        // StopButtonDisplayExpander above) AND the master ToggleSwitch itself (so
        // RefreshToggleSwitchDisabledVisual's own toggle.IsEnabled read below stays accurate) are set
        // here. The two per-mode sub-toggles use the same combined-gating pattern as
        // StopButtonDisplayCountdownRadioButton/CounterRadioButton above: locked while automation is
        // running, AND while the master toggle itself is off.
        PauseOnMovementExpander.IsEnabled = enabled;
        PauseOnMovementToggle.IsEnabled = enabled;
        PauseOnMovementAutoClickToggle.IsEnabled = enabled && PauseOnMovementToggle.IsOn;
        PauseOnMovementJiggleToggle.IsEnabled = enabled && PauseOnMovementToggle.IsOn;
    }

    /// <summary>
    /// Works around a native ToggleSwitch limitation: its default template's "Disabled" CommonState
    /// (see the installed WinUI SDK's generic.xaml, DefaultToggleSwitchStyle) recolors the Off-track
    /// via ColorAnimationUsingKeyFrames targeting (Shape.Fill/Stroke).(SolidColorBrush.Color) -
    /// {ThemeResource ToggleSwitchFillOffDisabled}/StrokeOffDisabled get resolved once, the moment the
    /// Storyboard runs, and baked as a literal Color onto the brush in place. That's not a live theme
    /// binding, so a toggle left sitting in Disabled (i.e. locked while automation runs - see
    /// SetInputsEnabled above) keeps showing whichever theme's gray was baked in whenever it was last
    /// disabled, even after the app/OS theme actually changes. The On-track side of the same animation
    /// bakes an accent-based color instead, which happens to look fine regardless of theme (the accent
    /// color itself doesn't change between Light/Dark) - that's why this only reads as visibly wrong on
    /// a toggle that's Off while locked, not one that's On.
    ///
    /// Re-entering "Disabled" re-runs its Storyboard, re-resolving the ThemeResources against
    /// whatever theme is current now and re-baking fresh colors - cheaper than reimplementing the
    /// disabled look with shadowed brushes (the fix DimmableLabel/PowerToggleAlternateButton needed
    /// elsewhere), since the native template's own Storyboard already does the right thing, it just
    /// needs to be told to run again. The "Normal" hop first is required: GoToState is a no-op if the
    /// control is already in the target state, so going straight back to "Disabled" wouldn't restart
    /// the Storyboard.
    ///
    /// Called from MainWindow's RootGrid.ActualThemeChanged handler - covers both an explicit Theme
    /// dropdown change and the OS theme changing while "Follow system" is selected, same as
    /// UpdateModeIndicators/UpdateRandomizeIntervalIndicator's own reason for hooking that event. That
    /// same handler also calls RefreshToggleSwitchDisabledVisual directly (it's internal, not private,
    /// for exactly this) for MainWindow's own AutoStopToggle - a native ToggleSwitch outside
    /// SettingsPanel entirely, but subject to this identical native-template limitation whenever it's
    /// locked while automation runs (see MainWindow.SetStopControlsEnabled).
    /// </summary>
    public void RefreshDisabledToggleSwitchesTheme()
    {
        RefreshToggleSwitchDisabledVisual(PauseOnMovementToggle);
        RefreshToggleSwitchDisabledVisual(PauseOnMovementAutoClickToggle);
        RefreshToggleSwitchDisabledVisual(PauseOnMovementJiggleToggle);
        RefreshToggleSwitchDisabledVisual(ShowStopButtonDisplayToggle);
    }

    internal static void RefreshToggleSwitchDisabledVisual(ToggleSwitch toggle)
    {
        if (!toggle.IsEnabled)
        {
            VisualStateManager.GoToState(toggle, "Normal", useTransitions: false);
            VisualStateManager.GoToState(toggle, "Disabled", useTransitions: false);
        }
    }

    private void StopButtonDisplayRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is RadioButton { Tag: string mode })
        {
            StopButtonDisplayChanged?.Invoke(this, EventArgs.Empty);
            ConfigService.Update(c => c.StopButtonDisplayMode = mode);
            UpdateStopButtonDisplayExpanderDescription();
        }
    }

    /// <summary>
    /// Gates StopButtonDisplayCountdownRadioButton/StopButtonDisplayCounterRadioButton - see
    /// AppConfig.ShowStopButtonDisplay's own comment. Only ever fires while automation isn't running
    /// (the toggle itself is locked via SetInputsEnabled whenever it is - see
    /// SetStopControlsEnabled's identical AutoStopToggle_Toggled precedent in MainWindow.xaml.cs for
    /// why that means this can set the two RadioButtons' IsEnabled from
    /// ShowStopButtonDisplayToggle.IsOn alone, with no separate "not running" check needed here).
    /// </summary>
    private void ShowStopButtonDisplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        StopButtonDisplayCountdownRadioButton.IsEnabled = ShowStopButtonDisplayToggle.IsOn;
        StopButtonDisplayCounterRadioButton.IsEnabled = ShowStopButtonDisplayToggle.IsOn;
        StopButtonDisplayChanged?.Invoke(this, EventArgs.Empty);
        ConfigService.Update(c => c.ShowStopButtonDisplay = ShowStopButtonDisplayToggle.IsOn);
        UpdateStopButtonDisplayExpanderDescription();
    }

    private void IntervalDisplayRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is RadioButton { Tag: string mode })
        {
            ShowAdvancedIntervalDisplayChanged?.Invoke(this, EventArgs.Empty);
            ConfigService.Update(c => c.ShowAdvancedIntervalDisplay = mode == "Advanced");
            UpdateIntervalDisplayExpanderDescription();
        }
    }

    /// <summary>
    /// The master on/off switch. Gates PauseOnMovementAutoClickToggle/PauseOnMovementJiggleToggle -
    /// mirrors ShowStopButtonDisplayToggle_Toggled's exact pattern for its own two RadioButtons - only
    /// ever fires while automation isn't running (the toggle itself is locked via SetInputsEnabled
    /// whenever it is, same as ShowStopButtonDisplayToggle), so this can set the two sub-toggles'
    /// IsEnabled from PauseOnMovementToggle.IsOn alone, with no separate "not running" check needed here.
    /// </summary>
    private void PauseOnMovementToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        PauseOnMovementAutoClickToggle.IsEnabled = PauseOnMovementToggle.IsOn;
        PauseOnMovementJiggleToggle.IsEnabled = PauseOnMovementToggle.IsOn;
        PauseOnMovementChanged?.Invoke(this, EventArgs.Empty);
        ConfigService.Update(c => c.PauseOnMovement = PauseOnMovementToggle.IsOn);
        UpdatePauseOnMovementExpanderDescription();
    }

    /// <summary>Persists whether "Pause on movement" applies to Auto click mode - see AppConfig.PauseOnMovementForAutoClick and IsPauseOnMovementActiveForMode.</summary>
    private void PauseOnMovementAutoClickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.PauseOnMovementForAutoClick = PauseOnMovementAutoClickToggle.IsOn);
        PauseOnMovementChanged?.Invoke(this, EventArgs.Empty);
        UpdatePauseOnMovementExpanderDescription();
    }

    /// <summary>Persists whether "Pause on movement" applies to Jiggle mode - see AppConfig.PauseOnMovementForJiggle and IsPauseOnMovementActiveForMode.</summary>
    private void PauseOnMovementJiggleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.PauseOnMovementForJiggle = PauseOnMovementJiggleToggle.IsOn);
        PauseOnMovementChanged?.Invoke(this, EventArgs.Empty);
        UpdatePauseOnMovementExpanderDescription();
    }

    /// <summary>
    /// Combines the master toggle with whichever per-mode sub-toggle applies to <paramref name="mode"/>
    /// into the single bool MouseAutomationEngine.Start actually needs - replaces the old plain
    /// PauseOnMovement property now that the engine itself no longer resolves Jiggle-only scoping
    /// internally (see MouseAutomationEngine.RunLoopAsync's pauseOnMovementActive). Called from
    /// MainWindow right where PauseOnMovement used to be read, passing the mode about to start.
    /// </summary>
    public bool IsPauseOnMovementActiveForMode(AutomationMode mode)
    {
        if (!PauseOnMovementToggle.IsOn)
        {
            return false;
        }

        return mode switch
        {
            AutomationMode.Click => PauseOnMovementAutoClickToggle.IsOn,
            AutomationMode.Jiggle => PauseOnMovementJiggleToggle.IsOn,
            _ => false
        };
    }

    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.CloseToTray = CloseToTrayToggle.IsOn);
        CloseToTrayChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        StartupTaskErrorTextBlock.Visibility = Visibility.Collapsed;

        if (StartWithWindowsToggle.IsOn)
        {
            var state = await StartupTaskService.EnableAsync();
            if (state != StartupTaskState.Enabled)
            {
                _isInitializing = true;
                StartWithWindowsToggle.IsOn = false;
                _isInitializing = false;
                StartupTaskErrorTextBlock.Text = state == StartupTaskState.DisabledByPolicy
                    ? "Startup is disabled by your organization's policy."
                    : "Windows blocked this - check Settings > Apps > Startup or Task Manager > Startup apps.";
                StartupTaskErrorTextBlock.Visibility = Visibility.Visible;
            }
        }
        else
        {
            await StartupTaskService.DisableAsync();
        }
    }

    private void ShowTaskbarProgressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.ShowTaskbarProgress = ShowTaskbarProgressToggle.IsOn);
        ShowTaskbarProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunAutomationOnLaunchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.RunAutomationOnLaunch = RunAutomationOnLaunchToggle.IsOn);
    }

    private void RandomizeIntervalOnLaunchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.RandomizeIntervalOnLaunch = RandomizeIntervalOnLaunchToggle.IsOn);
    }

    /// <summary>
    /// No _isInitializing guard needed here, unlike every RadioButton/ToggleSwitch handler in this
    /// file: LoadFromConfig sets PreferredModeDropDownButton.Content directly rather than checking a
    /// MenuFlyoutItem, so unlike RadioButton.IsChecked/ToggleSwitch.IsOn, this Click event only ever
    /// fires from a real user pick, never as a side effect of loading persisted state.
    /// </summary>
    private void PreferredModeMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string mode })
        {
            _preferredMode = mode;
            PreferredModeDropDownButton.Content = FormatPreferredMode(mode);
            ConfigService.Update(c => c.PreferredMode = mode);
        }
    }

    private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (sender is RadioButton { Tag: string theme })
        {
            ThemeSelectionChanged?.Invoke(this, theme);
            ConfigService.Update(c => c.Theme = theme);
            UpdateThemeExpanderDescription();
        }
    }

    /// <summary>
    /// Enters hotkey-recording mode: the next key HotkeyButton_KeyDown sees (that isn't itself a bare
    /// modifier) becomes the new global hotkey. Ignored while already recording, so a second click
    /// mid-capture can't start a redundant/overlapping capture.
    ///
    /// Also unregisters the current hotkey for the duration of the capture. Otherwise, if the user
    /// opens the recorder and then presses the *current* hotkey (e.g. out of habit, or because they
    /// didn't mean to open the recorder and don't know Escape cancels it), RegisterHotKey would
    /// intercept that keypress at the OS level as WM_HOTKEY instead of delivering it to this button -
    /// silently triggering Start/Stop while leaving the recorder stuck on "Press a key
    /// combination…" forever, since HotkeyButton_KeyDown never sees the key at all.
    /// </summary>
    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = true;
        UnregisterHotkey?.Invoke();
        HotkeyErrorTextBlock.Visibility = Visibility.Collapsed;
        HotkeyButtonLabel.Text = "Press a key combination…";
    }

    /// <summary>
    /// Captures the next key while recording: Escape cancels (reverts the label and re-registers the
    /// previous hotkey - HotkeyButton_Click unregistered it for the capture - with no save); a bare
    /// modifier key is ignored so recording keeps waiting for the actual key; any other key finalizes
    /// the combination together with whatever modifiers are currently held.
    ///
    /// On success: registers immediately via TryRegisterHotkey (which unregisters the old one
    /// first), persists it, and updates the label. On failure (already claimed by another app): rolls
    /// back to the previous hotkey so the app is never left with nothing registered, and shows an
    /// inline error instead of persisting.
    /// </summary>
    private void HotkeyButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            _isRecordingHotkey = false;
            HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            TryRegisterHotkey?.Invoke(_hotkeyModifiers, _hotkeyKey);
            return;
        }

        if (IsModifierKey(e.Key))
        {
            return;
        }

        uint modifiers = 0;
        if (IsKeyDown(VirtualKey.Control))
        {
            modifiers |= NativeMethods.MOD_CONTROL;
        }

        if (IsKeyDown(VirtualKey.Menu))
        {
            modifiers |= NativeMethods.MOD_ALT;
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            modifiers |= NativeMethods.MOD_SHIFT;
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            modifiers |= NativeMethods.MOD_WIN;
        }

        var virtualKey = (uint)e.Key;

        _isRecordingHotkey = false;

        var previousModifiers = _hotkeyModifiers;
        var previousKey = _hotkeyKey;

        if (TryRegisterHotkey?.Invoke(modifiers, virtualKey) == true)
        {
            _hotkeyModifiers = modifiers;
            _hotkeyKey = virtualKey;
            HotkeyButtonLabel.Text = FormatHotkey(modifiers, virtualKey);
            HotkeyErrorTextBlock.Visibility = Visibility.Collapsed;
            ConfigService.Update(c =>
            {
                c.HotkeyModifiers = modifiers;
                c.HotkeyKey = virtualKey;
            });
        }
        else
        {
            TryRegisterHotkey?.Invoke(previousModifiers, previousKey);
            HotkeyButtonLabel.Text = FormatHotkey(previousModifiers, previousKey);
            HotkeyErrorTextBlock.Text = "That shortcut is already in use by another app.";
            HotkeyErrorTextBlock.Visibility = Visibility.Visible;
        }
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Formats a modifiers bitmask + virtual-key code as a display string like "F6" or
    /// "Ctrl+Shift+F6". VirtualKey's own ToString() already reads correctly for the keys realistic
    /// hotkeys use (letters, digits, function keys), so no separate name table is needed.
    /// </summary>
    private static string FormatHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & NativeMethods.MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & NativeMethods.MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & NativeMethods.MOD_WIN) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(((VirtualKey)virtualKey).ToString());
        return string.Join("+", parts);
    }

    private static string FormatPreferredMode(string mode) => mode switch
    {
        "Click" => "Auto click",
        "Jiggle" => "Jiggle",
        _ => "Last used"
    };

}
