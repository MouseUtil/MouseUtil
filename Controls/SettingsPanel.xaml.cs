using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
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

    /// <summary>Raised (guarded by _isInitializing) whenever the Backdrop selection changes, so MainWindow can run ApplyBackdrop.</summary>
    public event EventHandler<string>? BackdropSelectionChanged;

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

    /// <summary>Raised only when AboutExpander is expanded (never on collapse), so MainWindow can scroll SettingsScrollViewer down to reveal it - SettingsScrollViewer lives in MainWindow.xaml, not in this UserControl, so this can't be done locally.</summary>
    public event EventHandler? AboutExpanderExpanded;

    /// <summary>
    /// Raised whenever one of the 5 non-About SettingsExpander cards is expanded - never on collapse -
    /// passing the expander itself so MainWindow can bring it into view within SettingsScrollViewer.
    /// Kept separate from AboutExpanderExpanded since AboutExpander's scroll-to-bottom is
    /// unconditional, while these 5 just ask the framework to reveal them.
    /// </summary>
    public event EventHandler<FrameworkElement>? SettingsExpanderExpanded;

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

    /// <summary>"LastUsed", "Click", or "Jiggle" - see AppConfig.PreferredMode's own comment. Backed by a plain field (rather than read back from PreferredModeDropDownButton) since DropDownButton/MenuFlyoutItem have no built-in "currently selected item" concept the way ComboBox does.</summary>
    public string PreferredMode => _preferredMode;

    /// <summary>"Normal", "Minimized", or "Tray" - see AppConfig.LaunchWindowMode's own comment. Same backing-field reasoning as _preferredMode above.</summary>
    public string LaunchWindowMode => _launchWindowMode;

    private bool _isInitializing;
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _isRecordingHotkey;
    private string _preferredMode = "LastUsed";
    private string _launchWindowMode = "Normal";

    /// <summary>Backing fields for ThemeDropDownButton/BackdropDropDownButton - same reasoning as _preferredMode's own comment (DropDownButton/MenuFlyoutItem have no built-in "currently selected item" concept).</summary>
    private string _theme = "System";
    private string _backdrop = "Mica";

    // Captured once at construction from SettingsRootPanel's own XAML-declared ChildrenTransitions -
    // ResetAfterClose swaps this out to null and back (see its own comment) rather than constructing
    // a fresh TransitionCollection each time.
    private readonly TransitionCollection? _reflowTransitions;

    public SettingsPanel()
    {
        InitializeComponent();

        _reflowTransitions = SettingsRootPanel.ChildrenTransitions;

        AboutExpander.Expanded += (_, _) => AboutExpanderExpanded?.Invoke(this, EventArgs.Empty);

        InterfaceExpander.Expanded += (s, _) => SettingsExpanderExpanded?.Invoke(this, (FrameworkElement)s!);
        IntervalDisplayExpander.Expanded += (s, _) => SettingsExpanderExpanded?.Invoke(this, (FrameworkElement)s!);
        StopButtonDisplayExpander.Expanded += (s, _) => SettingsExpanderExpanded?.Invoke(this, (FrameworkElement)s!);
        PauseOnMovementExpander.Expanded += (s, _) => SettingsExpanderExpanded?.Invoke(this, (FrameworkElement)s!);
        AppLaunchBehaviorExpander.Expanded += (s, _) => SettingsExpanderExpanded?.Invoke(this, (FrameworkElement)s!);

        AboutVersionText.Text = $"v{GetCurrentVersionString()}";

        LoadFromConfig();
        _ = LoadStartupTaskStateAsync();
    }

    /// <summary>Reads the running exe's file version, shown in AboutVersionText - same technique as MainWindow.GetCurrentVersionString.</summary>
    private static string GetCurrentVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        return !string.IsNullOrWhiteSpace(fileVersion) ? fileVersion : assembly.GetName().Version?.ToString() ?? "unknown";
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

        _preferredMode = config.PreferredMode;
        PreferredModeDropDownButton.Content = FormatPreferredMode(_preferredMode);

        _launchWindowMode = config.LaunchWindowMode;
        LaunchWindowDropDownButton.Content = FormatLaunchWindowMode(_launchWindowMode);
        LaunchWindowTrayItem.IsEnabled = CloseToTrayToggle.IsOn;

        (config.ShowAdvancedIntervalDisplay ? IntervalDisplayAdvancedRadioButton : IntervalDisplayBasicRadioButton).IsChecked = true;
        UpdateIntervalDisplayExpanderDescription();

        _hotkeyModifiers = config.HotkeyModifiers;
        _hotkeyKey = config.HotkeyKey;
        HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);

        _theme = config.Theme;
        _backdrop = config.Backdrop;
        ThemeDropDownButton.Content = FormatTheme(_theme);
        BackdropDropDownButton.Content = FormatBackdrop(_backdrop);
        UpdateInterfaceExpanderHeader();

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
    /// Sets InterfaceExpander's collapsed Description to both current selections. HeaderIcon is NOT
    /// touched here - it stays at its plain static glyph regardless of which Theme/Backdrop is selected.
    /// </summary>
    private void UpdateInterfaceExpanderHeader() =>
        InterfaceExpander.Description = $"{FormatTheme(_theme)}, {FormatBackdrop(_backdrop)}";

    private void UpdateIntervalDisplayExpanderDescription() =>
        IntervalDisplayExpander.Description = IntervalDisplayAdvancedRadioButton.IsChecked == true
            ? "Hours, Minutes, Seconds, Milliseconds"
            : "Minutes and Seconds";

    /// <summary>
    /// "Off" while the master toggle is off, otherwise which of "Auto click"/"Jiggle" the two
    /// sub-toggles apply to (comma-joined), or "On" if the master is on but neither sub-toggle is.
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
    /// Called immediately when the Settings overlay starts closing (before the slide-out animation) -
    /// cancels any in-progress hotkey capture and re-registers the previous hotkey (recording leaves
    /// it unregistered), so closing Settings mid-capture never leaves the app with no hotkey active.
    /// Runs right away rather than waiting for ResetAfterClose's delay, since the hotkey must be
    /// active immediately, not 300ms after the user has already left Settings.
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
    /// Called once the slide-out animation has fully finished - resets everything Settings should
    /// always come back to fresh: collapses every expandable card and clears the startup-task error
    /// banner. Delayed rather than run immediately (unlike HandleHostClosing) so none of this is
    /// visible mid-slide. The expander collapse also works around a SettingsExpander gap: disabling
    /// an expander while automation runs blocks its chevron but doesn't collapse it, so this
    /// guarantees a fresh, collapsed state on next open.
    ///
    /// SettingsRootPanel.ChildrenTransitions is set to null here and stays null - NOT restored at the
    /// end of this method. SettingsOverlay is Collapsed by the time this runs, so the Arrange-time
    /// reflow RepositionThemeTransition reacts to is deferred until SettingsOverlay becomes Visible
    /// again (see PrepareReflowTransitionsForReopen). Restoring the transition here immediately would
    /// leave it re-attached by then, animating the reflow in full view on reopen - exactly the bug
    /// this avoids.
    /// </summary>
    public void ResetAfterClose()
    {
        SettingsRootPanel.ChildrenTransitions = null;

        InterfaceExpander.IsExpanded = false;
        IntervalDisplayExpander.IsExpanded = false;
        StopButtonDisplayExpander.IsExpanded = false;
        PauseOnMovementExpander.IsExpanded = false;
        AppLaunchBehaviorExpander.IsExpanded = false;
        AboutExpander.IsExpanded = false;

        StartupTaskErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Called right after SettingsOverlay's Visibility flips back to Visible - forces the deferred
    /// layout pass (see ResetAfterClose) to run synchronously, before ChildrenTransitions is restored,
    /// so the catch-up reflow finishes before anything is on screen to show it happening. Only then is
    /// ChildrenTransitions restored, so interactive expand/collapse keeps animating normally.
    /// </summary>
    public void PrepareReflowTransitionsForReopen()
    {
        UpdateLayout();
        SettingsRootPanel.ChildrenTransitions = _reflowTransitions;
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

        // StopButtonDisplayExpander itself (not just its toggle/radio buttons) must be disabled too,
        // otherwise its chevron stays clickable while everything inside is locked.
        StopButtonDisplayExpander.IsEnabled = enabled;
        ShowStopButtonDisplayToggle.IsEnabled = enabled;
        StopButtonDisplayCountdownRadioButton.IsEnabled = enabled && ShowStopButtonDisplayToggle.IsOn;
        StopButtonDisplayCounterRadioButton.IsEnabled = enabled && ShowStopButtonDisplayToggle.IsOn;

        IntervalDisplayExpander.IsEnabled = enabled;

        // Both the wrapping SettingsExpander (dims Header/icon, blocks the chevron mid-run) and the
        // master ToggleSwitch itself are disabled here. The two sub-toggles use the same combined
        // gating as the RadioButtons above: locked while automation runs, and while the master is off.
        PauseOnMovementExpander.IsEnabled = enabled;
        PauseOnMovementToggle.IsEnabled = enabled;
        PauseOnMovementAutoClickToggle.IsEnabled = enabled && PauseOnMovementToggle.IsOn;
        PauseOnMovementJiggleToggle.IsEnabled = enabled && PauseOnMovementToggle.IsOn;
    }

    /// <summary>
    /// Works around a native ToggleSwitch limitation: its default template's "Disabled" state bakes
    /// {ThemeResource ToggleSwitchFillOffDisabled}/StrokeOffDisabled into a literal Color the moment
    /// the Storyboard runs, rather than a live theme binding - so a toggle left Disabled keeps
    /// showing whichever theme's gray was baked in even after the app/OS theme changes. The On-track
    /// side bakes an accent color instead, which looks fine regardless of theme, so this only matters
    /// for a toggle that's Off while locked.
    ///
    /// Re-entering "Disabled" re-runs the Storyboard, re-baking fresh colors against the current
    /// theme. The "Normal" hop first is required since GoToState is a no-op if already in the target
    /// state.
    ///
    /// Called from MainWindow's RootGrid.ActualThemeChanged handler, which also calls
    /// RefreshToggleSwitchDisabledVisual directly for MainWindow's own AutoStopToggle - a native
    /// ToggleSwitch outside SettingsPanel subject to the same limitation.
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
    /// (the toggle itself is locked via SetInputsEnabled whenever it is), so this can set the two
    /// RadioButtons' IsEnabled from ShowStopButtonDisplayToggle.IsOn alone.
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
    /// Lets MainWindow push a change to "Interval display" made via the Interval card's own toggle
    /// back into this panel's RadioButtons, keeping both in sync against
    /// AppConfig.ShowAdvancedIntervalDisplay. Deliberately does NOT raise
    /// ShowAdvancedIntervalDisplayChanged - the caller already updated its own state directly, so
    /// raising it back would be a redundant round-trip.
    /// </summary>
    public void SetShowAdvancedIntervalDisplay(bool advanced)
    {
        if (ShowAdvancedIntervalDisplay == advanced)
        {
            return;
        }

        _isInitializing = true;
        (advanced ? IntervalDisplayAdvancedRadioButton : IntervalDisplayBasicRadioButton).IsChecked = true;
        _isInitializing = false;

        ConfigService.Update(c => c.ShowAdvancedIntervalDisplay = advanced);
        UpdateIntervalDisplayExpanderDescription();
    }

    /// <summary>
    /// The master on/off switch. Gates PauseOnMovementAutoClickToggle/PauseOnMovementJiggleToggle -
    /// only ever fires while automation isn't running (locked via SetInputsEnabled), so this can set
    /// the two sub-toggles' IsEnabled from PauseOnMovementToggle.IsOn alone.
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
    /// into the single bool MouseAutomationEngine.Start needs. Called from MainWindow with the mode
    /// about to start.
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

    /// <summary>
    /// "System tray" (LaunchWindowTrayItem) only makes sense while this toggle is on - kept in sync
    /// here, and also set at startup in LoadFromConfig. If "System tray" is currently selected and
    /// this toggle turns off, auto-reverts "Launch window" back to "Normal" rather than leaving an
    /// unreachable selection persisted (same pattern as the two mutually-exclusive toggles elsewhere
    /// in this file).
    /// </summary>
    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.CloseToTray = CloseToTrayToggle.IsOn);
        CloseToTrayChanged?.Invoke(this, EventArgs.Empty);

        LaunchWindowTrayItem.IsEnabled = CloseToTrayToggle.IsOn;
        if (!CloseToTrayToggle.IsOn && _launchWindowMode == "Tray")
        {
            _launchWindowMode = "Normal";
            LaunchWindowDropDownButton.Content = FormatLaunchWindowMode(_launchWindowMode);
            ConfigService.Update(c => c.LaunchWindowMode = _launchWindowMode);
        }
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

    /// <summary>
    /// No _isInitializing guard needed here, unlike every RadioButton/ToggleSwitch handler in this
    /// file: LoadFromConfig sets Content directly rather than checking a MenuFlyoutItem, so this Click
    /// event only ever fires from a real user pick.
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

    /// <summary>No _isInitializing guard needed - same reasoning as PreferredModeMenuFlyoutItem_Click's own doc comment.</summary>
    private void LaunchWindowMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string mode })
        {
            _launchWindowMode = mode;
            LaunchWindowDropDownButton.Content = FormatLaunchWindowMode(mode);
            ConfigService.Update(c => c.LaunchWindowMode = mode);
        }
    }

    /// <summary>No _isInitializing guard needed - same reasoning as PreferredModeMenuFlyoutItem_Click's own doc comment.</summary>
    private void ThemeMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string theme })
        {
            _theme = theme;
            ThemeDropDownButton.Content = FormatTheme(theme);
            UpdateInterfaceExpanderHeader();
            ThemeSelectionChanged?.Invoke(this, theme);
            ConfigService.Update(c => c.Theme = theme);
        }
    }

    /// <summary>No _isInitializing guard needed - same reasoning as PreferredModeMenuFlyoutItem_Click's own doc comment.</summary>
    private void BackdropMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string backdrop })
        {
            _backdrop = backdrop;
            BackdropDropDownButton.Content = FormatBackdrop(backdrop);
            UpdateInterfaceExpanderHeader();
            BackdropSelectionChanged?.Invoke(this, backdrop);
            ConfigService.Update(c => c.Backdrop = backdrop);
        }
    }

    /// <summary>
    /// Enters hotkey-recording mode: the next non-modifier key HotkeyButton_KeyDown sees becomes the
    /// new global hotkey. Ignored while already recording.
    ///
    /// Also unregisters the current hotkey for the duration of the capture - otherwise pressing the
    /// current hotkey while recording would intercept it as WM_HOTKEY at the OS level instead of
    /// delivering it to this button, silently triggering Start/Stop while the recorder stays stuck.
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
    /// previous hotkey, no save); a bare modifier key is ignored; any other key finalizes the
    /// combination with whatever modifiers are currently held.
    ///
    /// On success: registers immediately via TryRegisterHotkey, persists it, and updates the label.
    /// On failure (already claimed by another app): rolls back to the previous hotkey and shows an
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

    private static string FormatLaunchWindowMode(string mode) => mode switch
    {
        "Minimized" => "Minimized",
        "Tray" => "System tray",
        _ => "Normal"
    };

    private static string FormatTheme(string theme) => theme switch
    {
        "Light" => "Light",
        "Dark" => "Dark",
        _ => "System"
    };

    private static string FormatBackdrop(string backdrop) => backdrop switch
    {
        "MicaAlt" => "Mica Alt",
        "Acrylic" => "Acrylic",
        _ => "Mica"
    };

}
