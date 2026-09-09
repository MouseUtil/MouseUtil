using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MouseUtil.Controls;
using MouseUtil.Interop;
using MouseUtil.Services;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MouseUtil;

/// <summary>
/// Which Auto Stop mode (if any) is currently configured - see AutoStopDialog/AutoStopButton_Click.
/// None means the feature has never been configured (AutoStopButton still shows its "Configure"
/// placeholder); Count and DateTime are mutually exclusive, enforced by AutoStopDialog's two
/// same-GroupName RadioButtons.
/// </summary>
public enum AutoStopMode
{
    None,
    Count,
    DateTime
}

public sealed partial class MainWindow : Window
{
    private const double WindowWidthDip = 400;
    private const double WindowHeightDip = 506;

    // The literal shrug emoticon shown instead of "Off"/"Stopped after N clicks" when this run's
    // very first automated click happened to land on and toggle off the Start/Stop button itself
    // (see PowerToggleButton_Unchecked / MouseAutomationEngine.WasFirstClickJustInjected). Verbatim
    // string so the backslash is literal, not an escape sequence.
    private const string SelfInflictedOffStatusText = @"¯\_(ツ)_/¯";

    private readonly MouseAutomationEngine _engine = new();
    private readonly UISettings _uiSettings = new();
    private bool _isInitializing;
    private string _themePreference = "System";
    private DateTime? _stopDateTime;

    // Fires once at the next local midnight to refresh AutoStopButtonLabel's Yesterday/Today/Tomorrow
    // relative-day wording (see UpdateAutoStopButtonLabel/ScheduleNextMidnightRefresh) - nothing else
    // naturally re-renders that label if the app just sits idle across a day boundary with a DateTime
    // auto-stop already configured, e.g. "Tomorrow 14:00" needs to become "Today 14:00" the instant the
    // day actually changes, not just the next time some unrelated UI interaction happens to touch it.
    private readonly DispatcherTimer _autoStopLabelMidnightTimer = new();

    // _autoStopMode drives both AutoStopButtonLabel's text and the actual stop condition
    // PowerToggleButton_Checked passes to the engine - it always starts at None on launch, regardless
    // of what was persisted last session, and only becomes Count/DateTime once the user explicitly
    // confirms AutoStopDialog with OK during THIS session (see AutoStopButton_Click). This is
    // deliberate: showing (or acting on) a stale "After 500 clicks" summary the user never re-reviewed
    // this session would be misleading. It's reset back to None whenever a run is started with Auto
    // Stop unchecked (see PowerToggleButton_Checked) - starting a run without it stays "unconfigured"
    // for good, not just for that run's duration, until re-confirmed via another OK.
    // _lastConfiguredAutoStopMode is the persisted seed used only to preselect AutoStopDialog's
    // RadioButtons the first time it's opened each session - it starts from config.AutoStopMode, then
    // tracks _autoStopMode from the first OK onward (deliberately NOT reset alongside _autoStopMode,
    // so the dialog still offers the last real choice instead of forcing a from-scratch reconfigure).
    private AutoStopMode _autoStopMode = AutoStopMode.None;
    private AutoStopMode _lastConfiguredAutoStopMode = AutoStopMode.None;
    private int _autoStopCount = 100;
    private bool _isJiggleModeSelected;

    // Guards ModeSegmentedControl_SelectionChanged from replaying the mode-switch side effects (icon
    // wiggle/spin animation, status/auto-stop refresh, persisted LastMode, tray icon update) whenever
    // UpdateModeIndicators sets ModeSegmentedControl.SelectedIndex purely to *sync* the control to
    // _isJiggleModeSelected (initial load, or any other visual refresh) - as opposed to an actual
    // user click on a segment or a tray-driven mode change (see SetSelectedMode), both of which should
    // still replay all of that. Only ever true for the duration of that one assignment.
    private bool _isSyncingModeSelection;

    // Randomize-interval toggle (RandomizeIntervalButton, in the Interval card's header row) -
    // deliberately never persisted to ConfigService, unlike most other toggles in this app: it
    // always starts Off on launch, regardless of what it was set to last session. See
    // RandomizeIntervalButton_Click/UpdateRandomizeIntervalIndicator and
    // MouseAutomationEngine.Start's randomizeInterval parameter for where this actually takes effect.
    private bool _isRandomizeIntervalEnabled;

    // Advanced-interval-display mode (Hours/Minutes/Seconds/Milliseconds fields, in place of the plain
    // Minutes/Seconds ones) - unlike _isRandomizeIntervalEnabled above, this one mirrors a genuinely
    // persisted setting (config.ShowAdvancedIntervalDisplay, owned by SettingsPanel's "Interval
    // display" dropdown - see UpdateAdvancedIntervalDisplayMode) rather than always starting
    // Off. Purely a UI display-mode switch either way - MinutesBox.Value/SecondsBox.Value stay the
    // actual source of truth (see AdvancedIntervalInputBox_TextChanged), so this field never feeds into
    // MouseAutomationEngine.
    private bool _isAdvancedIntervalDisplayEnabled;

    // Guards AdvancedIntervalBox_ValueChanged/AdvancedIntervalInputBox_TextChanged/
    // PopulateAdvancedIntervalFieldsFromBasic against reentrancy while one side of the Basic<->Advanced
    // conversion is programmatically writing into the other side's controls (e.g.
    // HoursBox.Value/AdvancedMinutesBox.Value etc. being populated from MinutesBox.Value/
    // SecondsBox.Value right after the toggle is checked) - without this, each ValueChanged/
    // TextChanged fired by that population would immediately try to convert back and overwrite
    // MinutesBox/SecondsBox mid-population with a transient, incomplete total.
    private bool _isSyncingAdvancedIntervalFields;

    // The mode currently selected in the UI (regardless of whether automation is running) - used
    // wherever a caller needs "whatever mode the mode switch is currently showing" as an AutomationMode
    // rather than the raw _isJiggleModeSelected bool, e.g. seeding TrayIconService's tooltip while
    // inactive (see UpdateState's mode parameter).
    private AutomationMode CurrentSelectedMode => _isJiggleModeSelected ? AutomationMode.Jiggle : AutomationMode.Click;

    // Click/jiggle action counter (see UpdatePowerButtonRunningDisplay). _completedActionCount counts
    // every action Engine_ActionPerformed reports, with no exclusion for the first one - the first
    // action after the startup countdown (or the first action fired immediately via the hotkey, see
    // skipStartupCountdown) counts as action #1, same as every action after it. The button switches
    // from "Stop" to showing the counter as soon as _completedActionCount > 0, rather than a separate
    // flag. _runningMode is captured once at Start() time (rather than re-read from
    // _isJiggleModeSelected) since ModeSegmentedControl is disabled mid-run anyway, but reading a captured
    // value is more explicit/robust. _isPointerOverPowerButton tracks hover state so the counter text
    // yields to "Stop" while the pointer is over the button.
    private int _completedActionCount;
    private AutomationMode _runningMode;
    private bool _isPointerOverPowerButton;

    // The user's own configured interval for this run (before any per-cycle randomize-interval draw -
    // see MouseAutomationEngine.GetEffectiveInterval), captured once at Start() time same as
    // _runningMode. StartImminentBlinkIfNeeded reads this to suppress the Imminent blink entirely on
    // short intervals (see ImminentBlinkMinimumInterval) - MinutesBox/SecondsBox are disabled mid-run
    // anyway, so a captured value is the same "explicit/robust over re-reading a live control" reasoning
    // as _runningMode.
    private TimeSpan _runningInterval;

    // Whether the pointer button is currently held down over PowerToggleButton or PowerToggleAlternateButton.
    // GetAlternateButtonForeground reads this for PowerToggleAlternateButton's press-tinted text color
    // (both its Paused and Starting states), applied directly to the icon/label elements (see the
    // "colorway" region below). Set by
    // PowerToggleButton_PointerPressed and cleared by PointerReleased/Canceled/CaptureLost, so an
    // interrupted press (e.g. capture stolen mid-drag) can never leave this stuck true.
    private bool _isPowerButtonPressed;

    // Countdown display mode's cache of the engine's most recent report - UpdatePowerButtonRunningDisplay
    // (via UpdatePowerButtonCountdownDisplay) needs these outside of a fresh Engine_StatusChanged tick too
    // (e.g. a hover enter/exit with no new engine report in between). _lastEngineStatusKind drives the
    // Paused/Starting/JiggleStarting branches there; _lastEngineStatusRemaining is the pause's resume
    // countdown once the engine's StillnessDisplayThreshold has passed (null before that, and reset to
    // null again on fresh movement - see UpdatePowerButtonCountdownDisplay's isPaused branch, which is
    // also where this drives the "Paused" vs "Resuming in Xs" button text split). Reset to Off/"Off"/null
    // at the start of every run (see PowerToggleButton_Checked) so a leftover Paused state from a previous
    // run can never leak into the brief window before the first real report arrives.
    private StatusKind _lastEngineStatusKind = StatusKind.Off;
    private string _lastEngineStatusText = "Off";
    private TimeSpan? _lastEngineStatusRemaining;

    // Global Start/Stop hotkey (F6 by default, user-configurable in Settings) - the actual
    // RegisterHotKey calls live here (see GlobalHotkeyService, InitializeGlobalHotkey,
    // HotkeyService_HotkeyPressed) since they require the WndProc subclass installed on this window;
    // recording a new combination/display/rollback-on-conflict is SettingsPanel's concern (wired via
    // its TryRegisterHotkey delegate - see InitializeSettingsPanel). _startTriggeredByHotkey is set
    // just before HotkeyService_HotkeyPressed programmatically checks PowerToggleButton, and consumed
    // (read then cleared) at the top of PowerToggleButton_Checked - this is what tells that handler to
    // skip the normal startup countdown and fire the first action immediately, since a real user
    // click on the button itself should still use the countdown as before. _startTriggeredByTrayAutoClick
    // is the same idea for the tray context menu's "Start Auto Click" item (see
    // TrayIconService_StartRequested) - only ever set for Click mode, since Jiggle mode already skips
    // the startup grace unconditionally regardless of this flag (see the needsStartupGrace check in
    // MouseAutomationEngine.RunLoopAsync), so "Start Jiggle" from the tray needs no equivalent.
    private readonly GlobalHotkeyService _hotkeyService = new();
    private bool _startTriggeredByHotkey;
    private bool _startTriggeredByTrayAutoClick;

    // Settings rows (see Controls/SettingsPanel.xaml) hosted permanently inside SettingsHost, within
    // SettingsOverlay. See InitializeSettingsPanel for the event/delegate contract wiring it back up
    // to this window's own window/system-level state.
    private readonly Controls.SettingsPanel _settingsPanel = new();

    // Guards ShowSettingsOverlay/SettingsBackButton_Click against re-entry while the slide
    // animation between them (see AnimatePanelTransition) is still running.
    private bool _isSettingsTransitioning;

    // Working copies of the values being edited while AutoStopDialog is open. Populated fresh from
    // the currently committed _stopDateTime/_autoStopCount (or sensible defaults, e.g. "now" for the
    // date+time) each time the dialog opens, updated live as the user interacts with
    // StopDatePicker/StopTimePicker/AutoStopCountBox, and only copied into _stopDateTime/_autoStopCount
    // (along with which RadioButton ended up checked, read directly at commit time - see
    // AutoStopButton_Click) if the user presses OK (ContentDialogResult.Primary). Cancel/Escape/
    // dismissing the dialog any other way simply discards these without touching committed state.
    private DateTime _stagedStopDate;
    private TimeSpan _stagedStopTime;
    private int _stagedAutoStopCount;

    // Set just before Engine_AutoStopped or HotkeyService_HotkeyPressed's stop path programmatically
    // flips PowerToggleButton off, and cleared by PowerToggleButton_Unchecked. Both set IsChecked
    // directly with no click involved at all, so without this flag, a scheduled auto-stop or a
    // hotkey-triggered stop that happens to land within MouseAutomationEngine.
    // WasFirstClickJustInjected's detection window of the run's first click could be misattributed
    // as that first click self-stopping it, wrongly showing the shrug status for what was actually a
    // deliberate, non-click-based stop.
    private bool _isProgrammaticToggleOff;

    // Set just before Close() is called from AppWindow_Closing's "Close anyway" path, so the
    // Closing event that call raises recognizes this as the already-confirmed close and lets it
    // through instead of cancelling and showing CloseConfirmationDialog a second time.
    private bool _isClosingConfirmed;

    // Set just before PowerToggleButton_Checked reverts an invalid start - Auto Stop enabled but
    // never configured (see _autoStopMode's declaration), or enabled with a DateTime that's already
    // passed - by setting IsChecked back to false itself, so the PowerToggleButton_Unchecked that
    // reverting synchronously raises knows to no-op instead of overwriting the error text just shown
    // / running normal stop side effects for a run that never actually started.
    private bool _isRejectingInvalidStart;

    // "Close to system tray" setting (SettingsPanel's CloseToTrayToggle, persisted there via
    // ConfigService) - mirrors the toggle's current IsOn state (kept in sync via
    // SettingsPanel.CloseToTrayChanged - see InitializeSettingsPanel) so AppWindow_Closing can read it
    // synchronously without touching the UI thread's control tree from inside that handler. See
    // TrayIconService for the actual Shell_NotifyIcon plumbing, and TrayIconService_ExitRequested
    // below for how "Exit" from the tray menu reuses CloseConfirmationDialog.
    private readonly TrayIconService _trayIconService = new();
    private bool _closeToTray;

    // "Show timer in taskbar" setting (SettingsPanel's ShowTaskbarProgressToggle, persisted there via
    // ConfigService) - mirrors the ITaskbarList3-driven progress bar overlaid on this app's taskbar
    // icon while automation runs (see Services/TaskbarProgressService, kept in sync via
    // SettingsPanel.ShowTaskbarProgressChanged - see InitializeSettingsPanel). _lastTaskbarProgress
    // holds the most recent numeric progress reported by the engine (see StatusChangedEventArgs.Progress)
    // so a "Paused" report with no countdown of its own (movement just detected) can still repaint the
    // bar amber/yellow at wherever it already was, instead of resetting it to 0.
    private readonly TaskbarProgressService _taskbarProgressService = new();
    private bool _showTaskbarProgress;
    private double _lastTaskbarProgress;

    public MainWindow()
    {
        InitializeComponent();

        // ButtonBase marks PointerPressed/PointerReleased e.Handled = true internally as part of its own
        // press/click handling (confirmed empirically: the native CheckedPressed VisualState reacts fine
        // to real input, but a plain XAML PointerPressed="..." attribute on PowerToggleButton never fired
        // at all), so a normal XAML-attribute subscription (equivalent to handledEventsToo: false) never
        // sees these events. AddHandler with handledEventsToo: true is the standard way around that - see
        // MainWindow.xaml's own comment on PowerToggleButton for why these four aren't XAML attributes.
        PowerToggleButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PowerToggleButton_PointerPressed), handledEventsToo: true);
        PowerToggleButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PowerToggleButton_PointerReleased), handledEventsToo: true);
        PowerToggleButton.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(PowerToggleButton_PointerCanceled), handledEventsToo: true);
        PowerToggleButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PowerToggleButton_PointerCaptureLost), handledEventsToo: true);

        // Same ButtonBase press-handling caveat applies to PowerToggleAlternateButton - reuses the exact
        // same handler methods as PowerToggleButton above (they only read/write the shared
        // _isPowerButtonPressed flag, not anything specific to which physical element was pressed).
        PowerToggleAlternateButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PowerToggleButton_PointerPressed), handledEventsToo: true);
        PowerToggleAlternateButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PowerToggleButton_PointerReleased), handledEventsToo: true);
        PowerToggleAlternateButton.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(PowerToggleButton_PointerCanceled), handledEventsToo: true);
        PowerToggleAlternateButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PowerToggleButton_PointerCaptureLost), handledEventsToo: true);

        // Set here rather than as a XAML Maximum="99999959" attribute on MinutesBox - see that
        // control's own XAML comment for why: WinUI's XAML compiler round-trips large double
        // attribute values through a 32-bit float in its compiled binary (XBF) encoding, and 99999959
        // (above float32's exact-integer range of 2^24) silently became 99999960 at runtime as a
        // result. A plain C# double assignment has no such precision loss. Set before LoadConfigIntoUi
        // so MinutesBox.Maximum is already correct by the time that method (or anything it triggers)
        // could read it.
        MinutesBox.Maximum = 99999959d;

        // Same XBF float32 precision issue as MinutesBox.Maximum above - 99999999 is also well past
        // 2^24, so this has to be a plain C# assignment too, not a XAML Maximum="99999999" attribute.
        AutoStopCountBox.Maximum = 99999999d;

        // Kept in sync with SingleInstanceService.MainWindowTitle (never a separate literal here) -
        // that's the exact string FindWindow searches for from a second-instance process, and it
        // differs for Debug builds specifically so a Debug build can run alongside an installed
        // Release build without either treating the other as a duplicate instance of itself.
        Title = SingleInstanceService.MainWindowTitle;
        SystemBackdrop = new MicaBackdrop();

        ConfigureWindowSizingAndMaximizeBehavior();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarGrid);

        // Default "Standard" caption-button height renders the OS min/close buttons short and flat.
        // Tall makes the OS draw them to match this titlebar's own 48px row height instead, so they
        // read as square rather than a shorter flat rectangle.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        SizeWindow();
        CenterOnScreen();
        InitializeSettingsPanel();

        LoadConfigIntoUi();
        InitializeAdvancedIntervalLiveSync();

        // AutoStopCountBox (the "After a number of clicks/jiggles" field in AutoStopDialog) gets the
        // same digits-only character filter and MaxLength cap as the interval fields above - reuses
        // SetInputBoxMaxLength/HookIntervalCharacterFilter as-is, not interval-specific despite living
        // alongside interval setup. No decimal point allowed here at all (this field is always a whole
        // count), and 8 matches its own Maximum's digit count (99999999).
        SetInputBoxMaxLength(AutoStopCountBox, maxLength: 8, allowDecimalPoint: false);
        SettingsVersionText.Text = $"v{GetCurrentVersionString()}";
        UpdateTitleBarCaptionSpacer();

        // MouseUtil.csproj's <ApplicationIcon> only embeds this icon into the compiled exe's PE
        // resources - that's what File Explorer/shortcuts/the pinned-taskbar icon read, but WinUI 3's
        // Window/AppWindow has no equivalent auto-binding for a *running* window's own icon, so
        // without this call Alt-Tab, Task View, and taskbar hover-preview thumbnails all fall back to
        // a generic default. Reuses the exact same Assets\app.ico already shipped for the tray icon
        // (see Services/TrayIconService.cs's LoadIcon) - no separate/duplicate icon file needed.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        InitializeGlobalHotkey();
        InitializeTrayIcon();
        _taskbarProgressService.Initialize(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        _engine.StatusChanged += Engine_StatusChanged;
        _engine.AutoStopped += Engine_AutoStopped;
        _engine.ActionPerformed += Engine_ActionPerformed;
        _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => _hotkeyService.Dispose();
        Closed += (_, _) => _trayIconService.Dispose();
        Closed += (_, _) => _taskbarProgressService.Dispose();
        Closed += (_, _) => _autoStopLabelMidnightTimer.Stop();

        _autoStopLabelMidnightTimer.Tick += (_, _) =>
        {
            UpdateAutoStopButtonLabel();
            ScheduleNextMidnightRefresh();
        };
        ScheduleNextMidnightRefresh();

        // ModeSegmentedControl's default look re-themes itself automatically (no manual snapshot-brush
        // refresh needed here the way the old ModeSwitchButton's hand-rolled box coloring required).
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateRandomizeIntervalIndicator();

            // Native ToggleSwitches disabled while automation is running (see SettingsPanel.
            // SetInputsEnabled/MainWindow.SetStopControlsEnabled) bake their Off-track color once, the
            // moment they're disabled - see RefreshDisabledToggleSwitchesTheme's own doc comment for why
            // that goes stale on a later theme change and needs forcing back to life here.
            _settingsPanel.RefreshDisabledToggleSwitchesTheme();
            SettingsPanel.RefreshToggleSwitchDisabledVisual(AutoStopToggle);
        };

        // AutoStopCountBox's InputBox part can be left with a stuck, invisible glyph layout the first
        // time this dialog is ever shown - see RefreshStaleNumberBoxTextLayout's own doc comment. Opened
        // fires only once the dialog is actually composed/shown (unlike XamlRoot assignment, which
        // happens earlier, before that's true - see AutoStopButton_Click's own comment on this), so this
        // is the right place to fix it.
        AutoStopDialog.Opened += (_, _) => RefreshStaleNumberBoxTextLayout(AutoStopCountBox);

        // Sets RandomizeIntervalIcon's initial AutomationProperties.Name/Foreground declaratively
        // from _isRandomizeIntervalEnabled's actual (always-false-on-launch) value, rather than
        // relying on the XAML defaults happening to already match it.
        UpdateRandomizeIntervalIndicator();

        // "Randomize interval when app launches" (SettingsPanel's "App launch behavior" expander) -
        // overrides RandomizeIntervalButton's own always-resets-Off-each-session default (see
        // _isRandomizeIntervalEnabled's own comment). Reuses RandomizeIntervalButton_CheckedChanged's
        // existing logic exactly like PowerToggleButton.IsChecked below reuses
        // PowerToggleButton_Checked's. Must run BEFORE that automation-start check, so
        // _isRandomizeIntervalEnabled is already correct by the time _engine.Start reads it.
        if (_settingsPanel.RandomizeIntervalOnLaunch)
        {
            RandomizeIntervalButton.IsChecked = true;
        }

        // "Start automatically" (SettingsPanel's "App launch behavior" expander) - applies
        // on every launch, not just ones Windows itself triggered via StartWithWindowsCard/
        // StartupTaskService. Mode is already correct by this point (LoadConfigIntoUi above set
        // _isJiggleModeSelected from PreferredMode), so this just has to toggle the button - reuses
        // 100% of PowerToggleButton_Checked's existing start logic, same as
        // TrayIconService_StartRequested's identical "force mode + start" pattern.
        if (_settingsPanel.RunAutomationOnLaunch)
        {
            PowerToggleButton.IsChecked = true;
        }
    }

    /// <summary>
    /// Subclasses this window's WndProc (see GlobalHotkeyService) and registers the hotkey currently
    /// persisted in config. If registration fails - e.g. another app already owns that exact
    /// combination - the hotkey simply doesn't fire until the user picks a different one in Settings;
    /// there's no other app state to roll back to at startup, unlike a failed re-registration during
    /// recording (see SettingsPanel.HotkeyButton_KeyDown).
    /// </summary>
    private void InitializeGlobalHotkey()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        _hotkeyService.AttachToWindow(hwnd);

        var config = ConfigService.Load();
        _hotkeyService.TryRegister(config.HotkeyModifiers, config.HotkeyKey);
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;

        // Single-instance enforcement (see App.OnLaunched/Services/SingleInstanceService): a second
        // launch attempt posts this message to bring THIS window to the foreground instead of ever
        // opening a duplicate. Reuses the WndProc subclass GlobalHotkeyService already installed for
        // WM_HOTKEY above, rather than adding a second one.
        _hotkeyService.RegisterMessageHandler(SingleInstanceService.ShowWindowMessageId, ActivateAndBringToForeground);
    }

    /// <summary>
    /// Wires the settings panel into this window (see its own field doc comment) and installs it
    /// into SettingsHost, inside SettingsOverlay, as its single, permanent home. Each subscription
    /// below is the MainWindow-side half of a contract SettingsPanel exposes for the one thing it
    /// can't do on its own - see each event's doc comment on SettingsPanel for why.
    /// </summary>
    private void InitializeSettingsPanel()
    {
        _settingsPanel.TryRegisterHotkey = (modifiers, key) => _hotkeyService.TryRegister(modifiers, key);
        _settingsPanel.UnregisterHotkey = () => _hotkeyService.Unregister();

        _settingsPanel.ThemeSelectionChanged += (_, theme) => ApplyTheme(theme);
        _settingsPanel.CloseToTrayChanged += (_, _) =>
        {
            _closeToTray = _settingsPanel.CloseToTray;
            UpdateTrayIconVisibility();
        };
        _settingsPanel.ShowTaskbarProgressChanged += (_, _) =>
        {
            _showTaskbarProgress = _settingsPanel.ShowTaskbarProgress;
            if (!_showTaskbarProgress)
            {
                _taskbarProgressService.Clear();
            }
        };
        _settingsPanel.PauseOnMovementChanged += (_, _) => ResetStatusToOffIfNotRunning();
        _settingsPanel.StopButtonDisplayChanged += (_, _) => UpdatePowerButtonRunningDisplay();
        _settingsPanel.ShowAdvancedIntervalDisplayChanged += (_, _) => UpdateAdvancedIntervalDisplayMode();

        SettingsHost.Children.Add(_settingsPanel);
    }

    /// <summary>Reads the running exe's file version, shown in Settings' SettingsVersionText.</summary>
    private static string GetCurrentVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        return !string.IsNullOrWhiteSpace(fileVersion) ? fileVersion : assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsOverlay();

    /// <summary>
    /// Opens the Settings overlay: moves focus onto SettingsBackButton rather than leaving it
    /// wherever it was in the main content underneath. SettingsButton itself is deliberately left
    /// alone here - it's a child of MainContentGrid, which already goes fully Collapsed (and is
    /// IsHitTestVisible=false for the whole transition, even before that) once Settings is showing,
    /// so it's already excluded from hit-testing and the visual tree without needing its own separate
    /// Visibility toggle. An explicit SettingsButton.Visibility = Collapsed/Visible pair used to exist
    /// here and in SettingsBackButton_Click - removed because it caused SettingsButton to pop in
    /// abruptly right at the end of the slide-back animation instead of sliding into place smoothly
    /// with the rest of MainContentGrid's content like everything else in it.
    /// </summary>
    private void ShowSettingsOverlay()
    {
        if (_isSettingsTransitioning)
        {
            return;
        }

        // MainContentGrid's Visibility.Collapsed no longer happens here directly - it's deferred to
        // AnimatePanelTransition's completion, once it has actually slid off screen (see that
        // method's own comment for why a Collapsed element can't be animated in the first place).
        AnimatePanelTransition(outgoing: MainContentGrid, incoming: SettingsOverlay, reverse: false,
            onCompleted: () => SettingsBackButton.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// Reverses ShowSettingsOverlay: slides the overlay back out (see ShowSettingsOverlay's own
    /// comment for why SettingsButton doesn't need any explicit handling here either).
    /// UpdateModeIndicators() in onCompleted re-syncs ModeSegmentedControl.SelectedIndex to
    /// _isJiggleModeSelected once the slide-back animation finishes, guarding against drift between the
    /// two - see that method's own comment. Also resets SettingsScrollViewer back to the top and calls
    /// _settingsPanel.ResetAfterClose() (collapses every expander, clears the startup-task error
    /// banner) here - not on the next open - so Settings always starts fresh however the user left it,
    /// however it - delayed 300ms (matching AnimatePanelTransition's own slide duration - see its own
    /// `duration`) rather than reset immediately, so none of that happens until the overlay has fully
    /// slid off screen instead of being visible mid-slide. disableAnimation is still true on the ChangeView
    /// itself since by the time it fires there's nothing left on screen for an animated scroll to show.
    /// </summary>
    private async void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsTransitioning)
        {
            return;
        }

        _settingsPanel.HandleHostClosing();

        AnimatePanelTransition(outgoing: SettingsOverlay, incoming: MainContentGrid, reverse: true,
            onCompleted: UpdateModeIndicators);

        await Task.Delay(300);
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        _settingsPanel.ResetAfterClose();
    }

    /// <summary>
    /// Slides `outgoing` off screen while sliding `incoming` into place, via the Composition API
    /// directly (Visual.Translation/Opacity), rather than a Storyboard - this is the same
    /// DirectComposition-backed engine WinUI's own Frame navigation transitions render through, just
    /// driven by hand since MainWindow has no Frame/Page to navigate. reverse=false is the "forward"
    /// direction (Main -> Settings): incoming enters from the right, outgoing exits to the left.
    /// reverse=true mirrors it (Settings -> Main): incoming enters from the left, outgoing exits right.
    ///
    /// Animates the Translation facade (enabled via SetIsTranslationEnabled), NOT Offset. Offset is
    /// the same property XAML's own layout/Arrange writes to position a panel normally, so animating
    /// it directly races Arrange - most visibly the very first time a panel is shown (it's been
    /// Collapsed, excluded from layout entirely, since app launch): that first-ever Arrange can land a
    /// frame after StartAnimation begins and silently overwrite Offset, killing the slide (only the
    /// Opacity animation would survive, so the panel just faded in place instead of sliding).
    /// Translation composes additively on top of whatever Offset Arrange assigns and Arrange never
    /// touches it, so there's no race regardless of timing.
    ///
    /// Both elements are forced Visible for the animation's duration - a Collapsed element is excluded
    /// from layout entirely (see CloseConfirmationDialog's own comment elsewhere in this file for the
    /// app's prior run-in with exactly this), so `outgoing` only becomes Collapsed again once its exit
    /// animation has actually finished, from the completion batch below. IsHitTestVisible is dropped to
    /// false on both elements for the same window, so nothing mid-slide can be clicked or tabbed into.
    /// </summary>
    private void AnimatePanelTransition(FrameworkElement outgoing, FrameworkElement incoming, bool reverse, Action? onCompleted = null)
    {
        _isSettingsTransitioning = true;

        var distance = (float)(RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : AppWindow.Size.Width);
        var incomingFrom = new Vector3(reverse ? -distance : distance, 0, 0);
        var outgoingTo = new Vector3(reverse ? distance : -distance, 0, 0);

        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = false;
        outgoing.IsHitTestVisible = false;

        ElementCompositionPreview.SetIsTranslationEnabled(outgoing, true);
        ElementCompositionPreview.SetIsTranslationEnabled(incoming, true);

        var compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var duration = TimeSpan.FromMilliseconds(300);

        var outgoingVisual = ElementCompositionPreview.GetElementVisual(outgoing);
        var incomingVisual = ElementCompositionPreview.GetElementVisual(incoming);

        incomingVisual.Properties.InsertVector3("Translation", incomingFrom);
        incomingVisual.Opacity = 0f;

        var outgoingTranslation = compositor.CreateVector3KeyFrameAnimation();
        outgoingTranslation.InsertKeyFrame(0f, Vector3.Zero);
        outgoingTranslation.InsertKeyFrame(1f, outgoingTo, easing);
        outgoingTranslation.Duration = duration;

        var outgoingOpacity = compositor.CreateScalarKeyFrameAnimation();
        outgoingOpacity.InsertKeyFrame(0f, 1f);
        outgoingOpacity.InsertKeyFrame(1f, 0f, easing);
        outgoingOpacity.Duration = duration;

        var incomingTranslation = compositor.CreateVector3KeyFrameAnimation();
        incomingTranslation.InsertKeyFrame(0f, incomingFrom);
        incomingTranslation.InsertKeyFrame(1f, Vector3.Zero, easing);
        incomingTranslation.Duration = duration;

        var incomingOpacity = compositor.CreateScalarKeyFrameAnimation();
        incomingOpacity.InsertKeyFrame(0f, 0f);
        incomingOpacity.InsertKeyFrame(1f, 1f, easing);
        incomingOpacity.Duration = duration;

        // A single ScopedBatch spanning all four animations gives one Completed event for the whole
        // transition, instead of racing four independent animations' own completion callbacks against
        // each other.
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        outgoingVisual.StartAnimation("Translation", outgoingTranslation);
        outgoingVisual.StartAnimation("Opacity", outgoingOpacity);
        incomingVisual.StartAnimation("Translation", incomingTranslation);
        incomingVisual.StartAnimation("Opacity", incomingOpacity);

        batch.Completed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                outgoingVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                outgoingVisual.Opacity = 1f;

                outgoing.IsHitTestVisible = true;
                incoming.IsHitTestVisible = true;

                _isSettingsTransitioning = false;
                onCompleted?.Invoke();
            });
        };
        batch.End();
    }

    /// <summary>
    /// Brings this window to the foreground regardless of its current state - restores it first if
    /// minimized, shows it if hidden (covers a future tray-icon "hide instead of close" feature, not
    /// just the normal/minimized cases that exist today), then forces it to the front. Called when a
    /// second launch attempt signals this instance via the message registered above.
    /// </summary>
    private void ActivateAndBringToForeground()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Wires up TrayIconService (see its own doc comment for the Shell_NotifyIcon details): left-click/
    /// "Show MouseUtil" simply restores the window, while "Exit" goes through
    /// TrayIconService_ExitRequested since it needs to run the same "automation still running?"
    /// confirmation AppWindow_Closing does. Finishes by calling UpdateTrayIconVisibility(), which shows
    /// the icon immediately if _closeToTray - already set from the persisted setting by
    /// LoadConfigIntoUi, called earlier in the constructor - is on.
    /// </summary>
    private void InitializeTrayIcon()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        _trayIconService.Initialize(hwnd, _hotkeyService);
        _trayIconService.UpdateState(isRunning: _engine.IsRunning, isPaused: false, mode: CurrentSelectedMode);
        _trayIconService.ShowRequested += (_, _) => ActivateAndBringToForeground();
        _trayIconService.ExitRequested += TrayIconService_ExitRequested;
        _trayIconService.StartRequested += TrayIconService_StartRequested;
        _trayIconService.StopRequested += TrayIconService_StopRequested;
        _trayIconService.TogglePauseOnMovementRequested += TrayIconService_TogglePauseOnMovementRequested;

        UpdateTrayIconVisibility();
    }

    /// <summary>
    /// Handles "Start Auto Click"/"Start Jiggle" from the tray context menu (only reachable while
    /// inactive - see TrayIconService.ShowContextMenu). Forces the mode selector to the requested mode
    /// (mirroring what a direct segment tap does, via the shared SetSelectedMode) and then starts
    /// automation by toggling PowerToggleButton exactly as a real click would - reusing 100% of
    /// PowerToggleButton_Checked's existing start logic (interval/auto-stop/pause-on-movement read live
    /// from the UI, engine start, input disabling, status text, tray icon update, etc.) rather than
    /// duplicating any of it. Same pattern HotkeyService_HotkeyPressed already uses for the global
    /// hotkey. The _engine.IsRunning guard is defensive - the menu item is disabled whenever running -
    /// but avoids ever re-entering Checked's logic if this somehow fires anyway.
    ///
    /// For Click mode specifically, this also arms _startTriggeredByTrayAutoClick before checking the
    /// button, so PowerToggleButton_Checked skips the startup countdown and fires the first click
    /// immediately - matching the global hotkey's existing behavior, since "Start Auto Click" from the
    /// tray implies the user isn't at the main window (possibly not even hovering it) any more than a
    /// hotkey press does. Jiggle mode is deliberately left alone: MouseAutomationEngine.RunLoopAsync's
    /// needsStartupGrace check already skips the startup grace for Jiggle unconditionally, so there's no
    /// countdown to skip and no flag to set here.
    /// </summary>
    private void TrayIconService_StartRequested(object? sender, AutomationMode mode)
    {
        if (_engine.IsRunning)
        {
            return;
        }

        if (mode == AutomationMode.Click)
        {
            _startTriggeredByTrayAutoClick = true;
        }

        SetSelectedMode(mode);
        PowerToggleButton.IsChecked = true;
    }

    /// <summary>
    /// Handles "Stop" from the tray context menu (only reachable while running - see
    /// TrayIconService.ShowContextMenu). Reuses PowerToggleButton_Unchecked's existing stop logic in
    /// full by toggling PowerToggleButton off, same as TrayIconService_StartRequested does for starting.
    /// </summary>
    private void TrayIconService_StopRequested(object? sender, EventArgs e)
    {
        if (!_engine.IsRunning)
        {
            return;
        }

        PowerToggleButton.IsChecked = false;
    }

    /// <summary>
    /// Handles "Pause on movement" from the tray context menu (only reachable while inactive -
    /// see TrayIconService.ShowContextMenu). Flips SettingsPanel's PauseOnMovementToggle itself
    /// (via TogglePauseOnMovement) rather than writing to AppConfig directly, so SettingsPanel's own
    /// Toggled handler stays the single place that persists the setting; this keeps the settings
    /// panel's toggle and the tray-driven change in sync in both directions with no separate
    /// bookkeeping.
    /// </summary>
    private void TrayIconService_TogglePauseOnMovementRequested(object? sender, EventArgs e)
    {
        if (_engine.IsRunning)
        {
            return;
        }

        _settingsPanel.TogglePauseOnMovement();
    }

    /// <summary>
    /// Shows or hides the tray icon to match _closeToTray - called once at startup (right after
    /// LoadConfigIntoUi has set _closeToTray) and again every time CloseToTrayToggle changes, so the
    /// icon appears/disappears immediately rather than only taking effect on the next close/reopen.
    /// </summary>
    private void UpdateTrayIconVisibility()
    {
        if (_closeToTray)
        {
            _trayIconService.Show();
        }
        else
        {
            _trayIconService.Hide();
        }
    }

    /// <summary>
    /// Handles "Exit" from the tray icon's context menu: always restores/activates the window first
    /// (so the user sees where CloseConfirmationDialog, if it appears, is coming from - see the feature
    /// spec), then applies the exact same "automation still running?" guard AppWindow_Closing uses for
    /// a normal close. Confirmed (or nothing running) -> actually exits, real close; cancelled -> does
    /// nothing further, window stays open right where it was just restored to.
    /// </summary>
    private async void TrayIconService_ExitRequested(object? sender, EventArgs e)
    {
        ActivateAndBringToForeground();

        if (_engine.IsRunning)
        {
            CloseConfirmationDialog.XamlRoot = Content.XamlRoot;
            var result = await CloseConfirmationDialog.ShowAsync();

            if (result != ContentDialogResult.Primary)
            {
                return;
            }
        }

        _isClosingConfirmed = true;
        Close();
    }

    /// <summary>
    /// Fires on the UI thread (WM_HOTKEY arrives via the subclassed WndProc, which already runs on
    /// it - no DispatcherQueue marshaling needed, unlike the engine's callbacks which run on its
    /// background loop thread). Toggles PowerToggleButton exactly as a real click would - reusing
    /// 100% of PowerToggleButton_Checked/_Unchecked's existing Start/Stop logic - except Checked
    /// consults _startTriggeredByHotkey to skip the startup countdown and perform the first action
    /// immediately, per the hotkey's required behavior.
    ///
    /// _startTriggeredByHotkey is only ever set when this press is about to START automation
    /// (willStart - i.e. the button isn't currently checked). Setting it unconditionally here,
    /// including on the STOP path, was the bug: a hotkey-triggered stop raises Unchecked, not
    /// Checked, so nothing would consume/clear the flag - it would sit there true and skip the
    /// countdown on whatever the *next* start turned out to be, even a plain button click. Only
    /// arming it on the path that's actually about to raise Checked keeps a hotkey-triggered stop
    /// from affecting a later, independently-triggered start.
    /// </summary>
    private void HotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        var willStart = PowerToggleButton.IsChecked != true;
        if (willStart)
        {
            _startTriggeredByHotkey = true;
        }
        else
        {
            // See the comment on _isProgrammaticToggleOff's declaration - this stop sets IsChecked
            // directly, with no click involved, so it must be excluded from the shrug-status check.
            _isProgrammaticToggleOff = true;
        }

        PowerToggleButton.IsChecked = willStart;
    }

    /// <summary>
    /// Sizes the trailing spacer column in AppTitleBarGrid to match the system's reserved caption
    /// button area (min/max/close), which ExtendsContentIntoTitleBar overlays on top of our content
    /// rather than laying our Grid out around. Without this, the ModeSelectorBar can render partly
    /// underneath the caption buttons. RightInset is reported in physical pixels, so it is converted
    /// to DIPs using the window's current DPI before being applied to the column's GridLength.
    /// </summary>
    private void UpdateTitleBarCaptionSpacer()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var rightInsetDip = AppWindow.TitleBar.RightInset / scale;
        CaptionButtonsSpacerColumn.Width = new GridLength(Math.Max(0, rightInsetDip));
    }

    /// <summary>
    /// The caption button reserved width can change after construction (e.g. window resize, DPI
    /// change when moved across monitors, or maximize/restore altering the presenter). Re-measure
    /// the spacer column whenever the AppWindow reports one of those changes.
    /// </summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPresenterChange)
        {
            DispatcherQueue.TryEnqueue(UpdateTitleBarCaptionSpacer);
        }
    }

    /// <summary>
    /// Guards against accidentally closing the window (X button, Alt+F4, taskbar close, etc.). Three
    /// cases, checked in order:
    /// 1. This is the second Closing raised by our own Close() call below (or by
    ///    TrayIconService_ExitRequested), after the user already confirmed (or nothing needed
    ///    confirming) - _isClosingConfirmed is set, let it through normally.
    /// 2. "Close to system tray" is on (_closeToTray) - cancel the close and hide the window instead of
    ///    exiting. No confirmation dialog here even if automation is running: the app isn't actually
    ///    exiting, so there is nothing to confirm - automation just keeps running in the tray.
    /// 3. Otherwise, original behavior: if automation is running, cancel and show
    ///    CloseConfirmationDialog, only proceeding to a real Close() if the user explicitly confirms;
    ///    if it isn't running, let the close through normally.
    /// </summary>
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isClosingConfirmed)
        {
            return;
        }

        if (_closeToTray)
        {
            args.Cancel = true;
            AppWindow.Hide();
            return;
        }

        if (!_engine.IsRunning)
        {
            return;
        }

        args.Cancel = true;

        CloseConfirmationDialog.XamlRoot = Content.XamlRoot;
        var result = await CloseConfirmationDialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            _isClosingConfirmed = true;
            Close();
        }
    }

    private void SizeWindow()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WindowWidthDip * scale), (int)(WindowHeightDip * scale)));
    }

    /// <summary>
    /// Disables both resizing (no drag-resize border/corners, no Aero-snap resize gestures) and
    /// maximize entirely (button disabled in the title bar, plus double-click-title-bar and Win+Up
    /// are blocked too - IsMaximizable governs all three), so the window stays fixed at the size
    /// SizeWindow() sets programmatically.
    /// </summary>
    private void ConfigureWindowSizingAndMaximizeBehavior()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
    }

    /// <summary>
    /// Always centers the window on the primary display's work area at the fixed default size.
    /// The window never remembers its position/size across launches - it is centered fresh every
    /// time. Never lets a windowing failure take the whole app down - worst case, it just skips
    /// positioning.
    /// </summary>
    private void CenterOnScreen()
    {
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var x = workArea.X + (workArea.Width - AppWindow.Size.Width) / 2;
            var y = workArea.Y + (workArea.Height - AppWindow.Size.Height) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // Positioning is a nicety, not core functionality - never let it crash startup.
        }
    }

    private void LoadConfigIntoUi()
    {
        _isInitializing = true;

        var config = ConfigService.Load();

        MinutesBox.Value = config.IntervalMinutes;
        SecondsBox.Value = config.IntervalSeconds;

        // SettingsPanel already loaded its own persisted state (including CloseToTray/
        // ShowTaskbarProgress/ShowAdvancedIntervalDisplay) when it was constructed in
        // InitializeSettingsPanel, called just before this method - read its mirrors here rather than
        // re-parsing config a second time.
        _closeToTray = _settingsPanel.CloseToTray;
        _showTaskbarProgress = _settingsPanel.ShowTaskbarProgress;

        // Applies the persisted Advanced-interval-display setting - called after MinutesBox/SecondsBox
        // above so PopulateAdvancedIntervalFieldsFromBasic reads the just-loaded values if this
        // restores into Advanced mode.
        UpdateAdvancedIntervalDisplayMode();

        ApplyTheme(config.Theme);

        // _settingsPanel.PreferredMode ("LastUsed" by default) can force a specific mode at launch
        // regardless of LastMode - see AppConfig.PreferredMode's own comment and SettingsPanel's
        // "Preferred mode" dropdown. Read from _settingsPanel rather than config directly, same as
        // _closeToTray/_showTaskbarProgress above, since it already parsed its own persisted state.
        _isJiggleModeSelected = _settingsPanel.PreferredMode switch
        {
            "Click" => false,
            "Jiggle" => true,
            _ => config.LastMode == "Jiggle"
        };
        UpdateModeIndicators();

        _lastConfiguredAutoStopMode = Enum.TryParse<AutoStopMode>(config.AutoStopMode, out var savedAutoStopMode) ? savedAutoStopMode : AutoStopMode.None;
        _autoStopCount = config.AutoStopCount;
        _autoStopMode = AutoStopMode.None;
        UpdateAutoStopButtonLabel();
        SetStopControlsEnabled(false);

        // NumberBox can raise ValueChanged one dispatcher tick after its Value is set here
        // (it defers until its control template is applied). Clear the guard from the back
        // of the dispatcher queue so any such deferred callback still sees _isInitializing = true.
        RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => _isInitializing = false);
    }

    private void ApplyTheme(string preference)
    {
        _themePreference = preference;

        RootGrid.RequestedTheme = preference switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        UpdateTitleBarButtonColors();
    }

    private void UpdateTitleBarButtonColors()
    {
        var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var foreground = isDark ? Colors.White : Colors.Black;

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(150, 255, 255, 255)
            : Color.FromArgb(150, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(25, 255, 255, 255)
            : Color.FromArgb(15, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(40, 255, 255, 255)
            : Color.FromArgb(30, 0, 0, 0);

        // A live OS theme flip (via UiSettings_ColorValuesChanged, which can fire mid-run while "Follow
        // system" is selected) needs to immediately refresh Countdown mode's paused-button colors to
        // whatever they resolve to under the new theme instead of waiting for the next engine tick -
        // UpdatePowerButtonRunningDisplay already no-ops harmlessly if the button isn't currently checked,
        // so this is safe to call unconditionally.
        UpdatePowerButtonRunningDisplay();
    }

    private void UiSettings_ColorValuesChanged(UISettings sender, object args)
    {
        if (_themePreference != "System")
        {
            return;
        }

        // Fires on a non-UI thread - must marshal back before touching the title bar / XAML tree.
        DispatcherQueue.TryEnqueue(UpdateTitleBarButtonColors);
    }

    /// <summary>
    /// Forces a genuine glyph-layout repaint of one NumberBox known to render invisible/stale text the
    /// first time it's actually shown: AutoStopCountBox (inside AutoStopDialog, a ContentDialog with no
    /// XamlRoot until first shown) and the four Advanced interval fields (inside AdvancedIntervalRow,
    /// Visibility="Collapsed" until the user enables it) - see this method's callers below.
    ///
    /// Root cause (confirmed empirically via direct property/pixel inspection - two earlier theories
    /// here, "Collapsed subtrees are excluded from the theme-repaint walk" and "the inner TextBox's
    /// {ThemeResource} Foreground goes stale," were both wrong: Foreground/Text/Value/Visibility/Opacity
    /// all read back already-correct at the moment the glyph still failed to render, confirmed via a
    /// property-changed-callback probe and direct pixel sampling of the rendered box, both before and
    /// after this method used to also copy a brush in here needlessly): the inner "InputBox" TextBox's
    /// text layout was computed for the FIRST time while it still had zero available width - both boxes
    /// get box.ApplyTemplate() forced eagerly in the constructor (InitializeAdvancedIntervalLiveSync/
    /// SetInputBoxMaxLength) while AdvancedIntervalRow is still Collapsed / AutoStopDialog has no
    /// XamlRoot, i.e. before either has ever been through a real Measure/Arrange pass. Neither a
    /// Visibility toggle nor fully detaching/reattaching the control from its parent's Children forces
    /// that cached zero-width layout to recompute; only an actual Text content change does - and even
    /// PopulateAdvancedIntervalFieldsFromBasic's own Value write (which does change Text) doesn't help,
    /// because it runs synchronously, in the same dispatch as the Visibility flip that reveals the row,
    /// before that row's own real Arrange pass (with real width) has actually happened yet.
    ///
    /// Fix: round-trip Text after the real Arrange pass has already happened - see this method's two
    /// callers (AdvancedIntervalRow: the deferred call in UpdateAdvancedIntervalDisplayMode, which runs
    /// on the next dispatch after the Visibility flip's own layout; AutoStopDialog: its Opened event,
    /// which fires only once the dialog is actually composed and shown).
    /// </summary>
    private static void RefreshStaleNumberBoxTextLayout(NumberBox box)
    {
        if (FindInputBox(box) is not { } inputBox)
        {
            return;
        }

        var currentText = inputBox.Text;
        inputBox.Text = currentText + " ";
        inputBox.Text = currentText;
    }

    private void SetInputsEnabled(bool enabled)
    {
        // Relies entirely on Segmented's own default Disabled visual state here - no custom
        // dimming/restyling, unlike the old ModeSwitchButton (see ModeSegmentedControl's own doc
        // comment in MainWindow.xaml).
        ModeSegmentedControl.IsEnabled = enabled;
        MinutesBox.IsEnabled = enabled;
        SecondsBox.IsEnabled = enabled;
        UpdateCompactSpinButtonIndicatorOpacity(MinutesBox, enabled);
        UpdateCompactSpinButtonIndicatorOpacity(SecondsBox, enabled);
        AutoStopToggle.IsEnabled = enabled;
        SetStopControlsEnabled(enabled && AutoStopToggle.IsOn);

        // IntervalCaptionTextBlock/AutoStopCaption are DimmableLabel controls (see
        // Controls/DimmableLabel.cs) - setting IsEnabled here drives their Normal/Disabled VisualState
        // transition declaratively, the same way MinutesBox/SecondsBox's own Header text dims when their
        // IsEnabled flips false.
        IntervalCaptionTextBlock.IsEnabled = enabled;
        AutoStopCaption.IsEnabled = enabled;

        // Locked while running so the randomize-interval behavior can't change out from under an
        // in-progress run. The two disabled cases still look deliberately different for the button's
        // own chrome (see RandomizeIntervalButton.Resources in MainWindow.xaml): Unchecked+Disabled
        // stays fully invisible (ToggleButtonBackgroundDisabled/BorderBrushDisabled are overridden to
        // Transparent - there's nothing to indicate when the setting is off), while Checked+Disabled
        // falls back to the default ToggleButtonStyle's own filled, muted-accent pill
        // (ToggleButtonBackgroundCheckedDisabled is no longer overridden here) with
        // ToggleButtonBorderBrushCheckedDisabled's own gray border kept on top as the one remaining
        // customization, so the user can still see at a glance that the setting is on even while it's
        // locked. RandomizeIntervalIcon's own Foreground/Opacity aren't set here at all, though -
        // they're set from within UpdateRandomizeIntervalIndicator instead (called below, after
        // IsEnabled is updated so it can see the new locked state), which leaves the icon fully
        // uncustomized (ClearValue'd Foreground, Opacity 1) while Checked+locked, versus a dimmed,
        // theme-neutral gray at Opacity 0.6 while Unchecked+locked (matching its still-invisible chrome)
        // - see that method's doc comment for the full reasoning.
        RandomizeIntervalButton.IsEnabled = enabled;
        UpdateRandomizeIntervalIndicator();

        // The four Hours/Minutes/Seconds/Milliseconds fields get the same locked-while-running
        // treatment as MinutesBox/SecondsBox above - the values within them can't change out from
        // under an in-progress run. (SettingsPanel's own "Interval display" dropdown is
        // locked the same way, independently, in SettingsPanel.SetInputsEnabled.)
        HoursBox.IsEnabled = enabled;
        AdvancedMinutesBox.IsEnabled = enabled;
        AdvancedSecondsBox.IsEnabled = enabled;
        MillisecondsBox.IsEnabled = enabled;

        // Hotkey/ShowActionCounter/PauseOnMovement (and their DimmableLabel captions) live in
        // SettingsPanel now.
        _settingsPanel.SetInputsEnabled(enabled);
    }

    private void SetStopControlsEnabled(bool enabled)
    {
        AutoStopButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Central place to set StatusTextBlock's text/tone together, including the "¯\_(ツ)_/¯" shrug
    /// (see PowerToggleButton_Unchecked) - every status string uses the app's normal font.
    /// StatusTextBlock is a StatusLabel (see Controls/StatusLabel.cs): passing a StatusTone enum
    /// instead of a Brush lets its own ControlTemplate VisualStates apply the actual
    /// {ThemeResource} color, so it stays correctly themed even if the theme changes while a
    /// non-Muted tone is showing.
    /// </summary>
    private void SetStatusText(string text, StatusTone tone)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Tone = tone;
    }

    private void PowerToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        // Consumed unconditionally, before the early-return below, so a stale true from some earlier
        // hotkey press or tray "Start Auto Click" can never leak into a later, genuinely
        // button-click-triggered start.
        var skipStartupCountdown = _startTriggeredByHotkey || _startTriggeredByTrayAutoClick;
        _startTriggeredByHotkey = false;
        _startTriggeredByTrayAutoClick = false;

        if (AutoStopToggle.IsOn)
        {
            // Refuse to start rather than either run with no actual stop condition the user didn't
            // intend, or start a run whose configured stop time has already passed - the engine's own
            // IsStopTimeReached would still catch the latter and stop it right away regardless, but
            // surfacing it here instead avoids a start-then-immediately-stop flash with no explanation.
            // See _isRejectingInvalidStart's declaration for why reverting IsChecked here is safe.
            var rejectionMessage = _autoStopMode switch
            {
                AutoStopMode.None => "Please configure or disable auto stop.",
                AutoStopMode.DateTime when _stopDateTime.HasValue && _stopDateTime.Value <= DateTime.Now
                    => "Auto stop date/time has already passed.",
                _ => null
            };

            if (rejectionMessage != null)
            {
                _isRejectingInvalidStart = true;
                SetStatusText(rejectionMessage, StatusTone.Accent);
                PowerToggleButton.IsChecked = false;
                return;
            }
        }

        var mode = CurrentSelectedMode;

        var minutes = ReadCommittedOrTypedValue(MinutesBox, fractionDigits: 0);
        var seconds = ReadCommittedOrTypedValue(SecondsBox, fractionDigits: 3);
        var totalSeconds = Math.Max(0.05, minutes * 60 + seconds);
        var interval = TimeSpan.FromSeconds(totalSeconds);

        DateTime? stopAt = null;
        int? stopAfterActionCount = null;
        if (AutoStopToggle.IsOn)
        {
            if (_autoStopMode == AutoStopMode.DateTime && _stopDateTime.HasValue)
            {
                stopAt = _stopDateTime.Value;
            }
            else if (_autoStopMode == AutoStopMode.Count)
            {
                stopAfterActionCount = _autoStopCount;
            }
        }
        else
        {
            // Auto Stop isn't active for this run - fall back to the unconfigured "Configure"
            // placeholder for good (see _autoStopMode's declaration), not just for this run's
            // duration, so PowerToggleButton_Unchecked's later UpdateAutoStopButtonLabel() call
            // doesn't bring back a stale summary the user just ran right past.
            _autoStopMode = AutoStopMode.None;
            UpdateAutoStopButtonLabel();
        }

        // Reset the counter every time automation starts - this run hasn't completed any counted
        // action yet. Also explicitly reset the status text to "Off": it's near-instantly overwritten
        // by the engine's own "Starting in Xs" StatusChanged report, but this keeps the state
        // unambiguous rather than relying on that timing.
        _completedActionCount = 0;
        _runningMode = mode;
        _runningInterval = interval;
        _lastEngineStatusKind = StatusKind.Off;
        _lastEngineStatusText = "Off";
        _lastEngineStatusRemaining = null;
        // Hide/reset PowerToggleAlternateButton left over from a previous run's Countdown-mode pause or
        // startup grace (see UpdateAlternateButtonDisplay) before this run's first status tick arrives -
        // otherwise a stop-while-paused/stop-during-startup followed immediately by a fresh start could
        // leave it visible for a frame. Icon/label/automation name are reset back to their Paused-ready
        // defaults too (Starting always overwrites its own content fresh every call, so it doesn't need a
        // default here), so a previous run's leftover "Resuming in Xs"/"Stop" text (from a pause that was
        // showing right when Stop was pressed) can never leak into a fresh run for a frame before
        // UpdateAlternateButtonDisplay overwrites it, the next time this run actually pauses. The three
        // shadowed background brushes aren't reset here - UpdateAlternateButtonDisplay/
        // ApplyAlternateButtonBackgroundBrushes always repaints them before this button becomes visible
        // again regardless, so there's nothing for a stale color to leak into.
        ShowPowerButton(AlternateButtonState.Hidden);
        StopImminentBlink(); // in case a previous run's Countdown-mode Imminent blink was still fading
        PowerToggleAlternateLabel.ClearValue(TextBlock.FontSizeProperty); // in case a previous run shrank it
        PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // in case a previous run shrank it
        PowerToggleAlternateIcon.Glyph = "\uF8AE"; // PauseBold glyph - UpdateAlternateButtonDisplay's own default.
        PowerToggleAlternateLabel.Text = "Paused";
        AutomationProperties.SetName(PowerToggleAlternateButton, "Power, Stop");
        SetStatusText("Off", StatusTone.Muted);

        SetInputsEnabled(false);
        PowerToggleIcon.Glyph = "\uEE95"; // Stop glyph - pressing the pill now would stop the engine.
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.Text = "Stop";
        AutomationProperties.SetName(PowerToggleButton, "Power, Stop");

        _engine.Start(mode, interval, stopAt, stopAfterActionCount, _settingsPanel.IsPauseOnMovementActiveForMode(mode), _isRandomizeIntervalEnabled, skipStartupCountdown);
        _trayIconService.UpdateState(isRunning: true, isPaused: false, mode: mode);
    }

    // NumberBox only re-parses typed input into Value on focus loss/Enter, so a hotkey-triggered start
    // (which never moves focus) would otherwise read the last-committed Value and ignore text the user
    // just typed but hasn't blurred away from yet. NumberBox's own Text DP turned out not to be a
    // reliable stand-in either - measured (via logging) up to 60+ms of lag behind what's on screen,
    // presumably from its own internal validation being debounced/async. Reading the template's actual
    // "InputBox" TextBox part directly is the one source with zero indirection - it's the literal
    // control the user is typing into.
    //
    // fractionDigits truncates (floors, never rounds) whatever was parsed to that many decimal places
    // before clamping to box.Minimum/box.Maximum - this is what makes a hotkey-triggered start use
    // e.g. 570 (not 570.002) for MinutesBox or 5.642 (not 5.6427) for SecondsBox, mirroring the
    // truncation IntervalBox_ValueChanged applies at commit time, but live, for text that was typed
    // and never committed. See TruncateToFractionDigits.
    private static double ReadCommittedOrTypedValue(NumberBox box, int fractionDigits)
    {
        var liveText = FindInputBoxText(box) ?? box.Text;

        // InvariantCulture, not CurrentCulture: confirmed empirically (isolated console test, not
        // assumed) that CultureInfo.CurrentCulture here is actively dangerous on a machine whose
        // Windows region uses ',' as its decimal separator and '.' as its thousands separator (e.g.
        // de-DE, ro-RO) - NumberStyles.Any's AllowThousands means double.TryParse doesn't reject a
        // '.' it doesn't recognize as a decimal point, it silently treats it as a thousands separator
        // and strips it instead: "32.5" parsed as TRUE with result 325, "59.999" parsed as TRUE with
        // result 59999 - both wrong by roughly 1000x, not a parse failure. (A space-grouped culture
        // like fr-FR instead fails outright - false/0 - which at least falls through safely to the
        // box.Value fallback below.) Only SecondsBox ever has a literal '.' in its text at all (the
        // other five fields are digits-only, so this swap changes nothing for them), and
        // HookIntervalCharacterFilter's comma-to-'.' normalization guarantees SecondsBox's own Text
        // never contains ',' either - so its Text is always canonically invariant-formatted
        // regardless of the OS locale, and parsing it should be too, rather than depending on
        // whatever the OS happens to be set to.
        if (double.TryParse(liveText, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) &&
            !double.IsNaN(parsed))
        {
            var clamped = Math.Clamp(parsed, box.Minimum, box.Maximum);
            return TruncateToFractionDigits(clamped, fractionDigits);
        }

        return double.IsNaN(box.Value) ? 0 : TruncateToFractionDigits(box.Value, fractionDigits);
    }

    /// <summary>
    /// Truncates (floors toward zero - e.g. 56.8 -> 56, never rounds to 57) value to fractionDigits
    /// decimal places. Shared by every interval-field truncation path (ReadCommittedOrTypedValue,
    /// IntervalBox_ValueChanged, AdvancedIntervalBox_ValueChanged, AdvancedIntervalInputBox_TextChanged)
    /// so the "truncate, don't round" rule lives in exactly one place. NumberBox's own
    /// NumberFormatter/FractionDigits can't substitute for this: that only affects the displayed Text
    /// (via NumberBox's internal UpdateTextToValue), never writes back into .Value, and rounds rather
    /// than truncates regardless of configuration.
    /// </summary>
    private static double TruncateToFractionDigits(double value, int fractionDigits)
    {
        var scale = Math.Pow(10, fractionDigits);
        return Math.Truncate(value * scale) / scale;
    }

    private static string? FindInputBoxText(DependencyObject root) => FindInputBox(root)?.Text;

    // Used both to read live, uncommitted text (ReadCommittedOrTypedValue/FindInputBoxText above) and,
    // for the four Advanced interval NumberBoxes, to hook the inner TextBox's own live TextChanged
    // event directly (see InitializeAdvancedIntervalLiveSync) - NumberBox.ValueChanged only fires on
    // commit (blur/Enter/programmatic set), never on every keystroke, but this inner TextBox is a real
    // TextBox under the hood, so its own TextChanged fires live.
    private static TextBox? FindInputBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Name: "InputBox" } inputBox)
            {
                return inputBox;
            }

            if (FindInputBox(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // "PopupIndicator" is the small combined up/down chevron glyph NumberBox's own ControlTemplate
    // shows at rest in SpinButtonPlacementMode="Compact" (see MinutesBox/SecondsBox). It's a plain
    // TextBlock with a hardcoded ThemeResource Foreground - the template's own Disabled VisualState
    // only dims HeaderContentPresenter (the "Minutes"/"Seconds" label), never this glyph, so it stays
    // at full opacity even while the NumberBox is IsEnabled=false. Found and dimmed manually from
    // SetInputsEnabled below for that reason.
    private static TextBlock? FindPopupIndicator(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Name: "PopupIndicator" } popupIndicator)
            {
                return popupIndicator;
            }

            if (FindPopupIndicator(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Dims MinutesBox/SecondsBox's own Compact spin-button chevron (see FindPopupIndicator above) to
    /// match their IsEnabled state - 0.3 rather than UpdateRandomizeIntervalIndicator's own 0.6
    /// locked-opacity value, since this glyph reads as more prominent than that icon at rest and needs
    /// to dim further to look equivalently disabled next to the header/value text beside it.
    /// </summary>
    private static void UpdateCompactSpinButtonIndicatorOpacity(NumberBox box, bool enabled)
    {
        if (FindPopupIndicator(box) is { } popupIndicator)
        {
            popupIndicator.Opacity = enabled ? 1 : 0.3;
        }
    }

    private void PowerToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isRejectingInvalidStart)
        {
            _isRejectingInvalidStart = false;
            return;
        }

        // Shows the shrug only if this run's very first automated click is what caused this stop
        // (the OS cursor happened to already be over the Start/Stop button) - never for a later
        // self-inflicted click, and never for the scheduled-auto-stop/hotkey-stop paths, excluded
        // via _isProgrammaticToggleOff (see its declaration).
        var showShrug = !_isProgrammaticToggleOff && _engine.WasFirstClickJustInjected();
        _isProgrammaticToggleOff = false;

        _engine.Stop();
        _trayIconService.UpdateState(isRunning: false, isPaused: false, mode: CurrentSelectedMode);
        _taskbarProgressService.Clear();
        _lastTaskbarProgress = 0;

        // Discard any status hold left over from a fast start-then-stop, so
        // ReleaseStatusHoldAfterDelayAsync's still-pending timer can't later overwrite the
        // "Stopped after N jiggle" text below with a stale buffered "Jiggling in X" tick.
        _statusHoldActive = false;
        _pendingStatusAfterHold = null;

        // Re-renders from _autoStopMode's current value - a no-op if this run left it untouched
        // (Auto Stop was enabled/configured), or reflects the "Configure" placeholder if this run's
        // Checked handler just reset it to None because Auto Stop was disabled.
        UpdateAutoStopButtonLabel();

        SetInputsEnabled(true);
        StopImminentBlink(); // Stop pressed mid-blink shouldn't leave PowerToggleLabel faded for the Play/"Start" text below.
        // Icon/label flip to Play/"Start" synchronously here, same tick as always - PowerToggleButton is
        // correct and ready the instant it becomes visible, whether that's right now (below) or, if we
        // were paused, at the exact moment the stop-while-paused transition below finishes.
        PowerToggleIcon.Glyph = "\uE768"; // Play glyph - pressing the pill now would start the engine.
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // in case this run's countdown text had shrunk it
        PowerToggleLabel.Text = "Start";
        AutomationProperties.SetName(PowerToggleButton, "Power, Start");

        // Reveal PowerToggleButton immediately, whether or not we were paused - PowerToggleAlternateButton's
        // Paused color is the same accent color as PowerToggleButton's own Checked background now (see
        // GetAlternateButtonBackground/GetAlternateButtonForeground), so there's no jarring color mismatch
        // to mask with an intermediate "fake Start" look on the way there anymore (an earlier version of
        // this played one - see git history on this branch if that's ever worth revisiting).
        ShowPowerButton(AlternateButtonState.Hidden);

        if (showShrug)
        {
            SetStatusText(SelfInflictedOffStatusText, StatusTone.Muted);
        }
        else
        {
            // Shows the counter result instead of plain "Off" whenever at least one counted action
            // actually happened, regardless of "Stop button display"'s toggle/mode state - every
            // combination now actively tracks/displays the count somewhere (the button in Counter mode,
            // the status bar in Countdown mode and whenever the toggle is off), so there's no "hide it
            // entirely" state left to gate this on.
            var text = _completedActionCount > 0
                ? $"Stopped after {FormatActionCount(_completedActionCount, _runningMode)}"
                : "Off";
            SetStatusText(text, StatusTone.Muted);
        }
    }

    /// <summary>
    /// PowerToggleAlternateButton's Click handler (both its Paused and Starting states) - just flips
    /// PowerToggleButton's own IsChecked off, which raises PowerToggleButton_Unchecked and runs the entire
    /// stop path (engine.Stop(), status text, swapping this button back out, everything) exactly as if
    /// PowerToggleButton itself had been unchecked. Nothing stop-related is duplicated here.
    /// </summary>
    private void PowerToggleAlternateButton_Click(object sender, RoutedEventArgs e)
    {
        PowerToggleButton.IsChecked = false;
    }

    private void Engine_ActionPerformed(object? sender, ActionPerformedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Every action counts, including the first one fired immediately after the startup
            // countdown (or immediately on Start, when triggered via the hotkey) - it's action #1,
            // not a free/uncounted kickoff. This is also what flips the button over to showing the
            // counter instead of "Stop", once _completedActionCount > 0 (see UpdatePowerButtonRunningDisplay).
            _completedActionCount++;
            UpdatePowerButtonRunningDisplay();
        });
    }

    private void PowerToggleButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPowerButton = true;
        UpdatePowerButtonRunningDisplay();
    }

    private void PowerToggleButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPowerButton = false;
        UpdatePowerButtonRunningDisplay();
    }

    private void PowerToggleButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPowerButtonPressed = true;
        UpdatePowerButtonRunningDisplay();
    }

    private void PowerToggleButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPowerButtonPressed = false;
        UpdatePowerButtonRunningDisplay();
    }

    private void PowerToggleButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPowerButtonPressed = false;
        UpdatePowerButtonRunningDisplay();
    }

    private void PowerToggleButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPowerButtonPressed = false;
        UpdatePowerButtonRunningDisplay();
    }

    /// <summary>
    /// Whether PowerToggleButton is currently showing the live Countdown treatment - true only when
    /// ShowStopButtonDisplayToggle is on AND the radio buttons beneath it are set to Countdown. Centralizes
    /// that compound check for every call site below that used to compare StopButtonDisplay directly
    /// against StopButtonDisplayMode.Countdown/None before the toggle existed - back when "off" was
    /// itself a third dropdown value (None) rather than an orthogonal toggle, checking the dropdown's
    /// value alone was sufficient; now both need to agree.
    /// </summary>
    private bool IsCountdownDisplayActive =>
        _settingsPanel.IsStopButtonDisplayShown && _settingsPanel.StopButtonDisplay == Controls.StopButtonDisplayMode.Countdown;

    /// <summary>
    /// Refreshes PowerToggleButton's live label/icon/background while the engine is running - the single
    /// place that decides what the button shows, branching on SettingsPanel's ShowStopButtonDisplayToggle/
    /// StopButtonDisplay (see Controls/SettingsPanel.xaml's toggle and "Stop button display" dropdown):
    ///
    /// - Toggle off: always plain "Stop" (icon visible), unconditionally - see UpdatePowerButtonNoneDisplay.
    ///   Both the countdown and the count live on the status bar instead in this mode (see GetStatusBarText).
    /// - Toggle on, Countdown mode: shows the engine's own live countdown text (e.g. "Clicking in 4s") in
    ///   place of "Stop" - see UpdatePowerButtonCountdownDisplay for the paused/hover special cases.
    /// - Toggle on, Counter mode: unchanged from before Countdown mode existed - "Stop" (icon visible) until
    ///   the first action fires, then the running "{count} click(s)/jiggle(s)" text (icon collapsed) once
    ///   _completedActionCount > 0, yielding back to "Stop" while the pointer hovers the button.
    ///
    /// No-ops while the button isn't checked/running - hovering while stopped shouldn't do anything.
    /// </summary>
    private void UpdatePowerButtonRunningDisplay()
    {
        if (PowerToggleButton.IsChecked != true)
        {
            return;
        }

        if (!_settingsPanel.IsStopButtonDisplayShown)
        {
            UpdatePowerButtonNoneDisplay();
            return;
        }

        if (IsCountdownDisplayActive)
        {
            UpdatePowerButtonCountdownDisplay();
            return;
        }

        StopImminentBlink(); // Counter mode has no Imminent blink of its own - guards a mid-run switch away from Countdown.
        var showCounter = _completedActionCount > 0 && !_isPointerOverPowerButton;
        PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
        PowerToggleLabel.Text = showCounter ? FormatActionCount(_completedActionCount, _runningMode) : "Stop";
        PowerToggleIcon.Visibility = showCounter ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// ShowStopButtonDisplayToggle-off half of UpdatePowerButtonRunningDisplay: PowerToggleButton always
    /// shows plain "Stop" with its Stop icon, unconditionally, for the entire run - never the click/jiggle
    /// count (Counter mode) and never a countdown or the separate PowerToggleAlternateButton (Countdown
    /// mode's pause-on-movement and Click-mode startup-grace elements). Both the countdown-
    /// until-next-action and the running click/jiggle count move to the status bar instead (see
    /// GetStatusBarText).
    ///
    /// ShowPowerButton(AlternateButtonState.Hidden) is called unconditionally/idempotently here (harmless
    /// once PowerToggleButton is already the shown element) specifically so that turning the toggle off
    /// *while* PowerToggleAlternateButton is currently showing (left over from Countdown mode) immediately
    /// reverts to the plain button - StopButtonDisplayChanged already calls UpdatePowerButtonRunningDisplay()
    /// on every toggle/dropdown change while running (see InitializeSettingsPanel), so this is the only
    /// place that needs to handle that hand-off. PowerToggleLabel.ClearValue(FontSizeProperty) undoes a
    /// previous Countdown-mode run's ApplyCountdownFontSize shrink, in case the display mode was switched
    /// mid-run.
    /// </summary>
    private void UpdatePowerButtonNoneDisplay()
    {
        ShowPowerButton(AlternateButtonState.Hidden);
        StopImminentBlink(); // This state has no Imminent blink of its own - guards a mid-run switch away from Countdown.
        PowerToggleIcon.Glyph = "\uEE95"; // Stop glyph
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty);
        PowerToggleLabel.Text = "Stop";
    }

    /// <summary>
    /// IsCountdownDisplayActive's half of UpdatePowerButtonRunningDisplay (see that method for Counter
    /// mode and the much simpler toggle-off state - UpdatePowerButtonNoneDisplay - which never touches
    /// this method or PowerToggleAlternateButton at all). Delegates entirely to UpdateAlternateButtonDisplay whenever the
    /// engine's last report is StatusKind.Paused (pause-on-movement) or StatusKind.Starting
    /// (Click mode's 3-second startup grace) - that method owns everything about PowerToggleAlternateButton
    /// (visibility, colors, text/icon content) for both. Both checks come before the hover check
    /// deliberately: Starting must never yield to hover's "Stop" text (see UpdateAlternateButtonDisplay's
    /// own doc comment for why), so it can't be handled alongside JiggleStarting further down where hover
    /// already won. Otherwise this operates on PowerToggleButton exactly as it always has: swapping
    /// PowerToggleAlternateButton back out instantly (no animation - the one animated transition in this
    /// rewrite is the stop-while-paused exit, driven from PowerToggleButton_Unchecked, never from here) if
    /// a resume/startup grace just ended, then falling through to the same hover/JiggleStarting/live-
    /// countdown branches as before.
    ///
    /// Label/icon content logic (what text/icon to show, as opposed to which element or what color) is
    /// otherwise unchanged from before this rewrite: hovering always wins and shows the Stop glyph +
    /// "Stop" (for every remaining case reaching this far - Running/Imminent/JiggleStarting); everything
    /// else shows the engine's own live status text verbatim with the icon collapsed. JiggleStarting (Jiggle
    /// mode's one-shot "Starting now", which unlike Click mode's Starting has no real countdown to show)
    /// still shows the plain Stop glyph + "Stop" rather than the engine's live status text, matching
    /// Counter mode's behavior for this same phase and avoiding a duplicate of the same text already shown
    /// on the status bar (see GetStatusBarText's _completedActionCount == 0 fallback to e.Text).
    /// </summary>
    private void UpdatePowerButtonCountdownDisplay()
    {
        if (_lastEngineStatusKind == StatusKind.Paused)
        {
            StopImminentBlink();
            UpdateAlternateButtonDisplay(AlternateButtonState.Paused);
            return;
        }

        if (_lastEngineStatusKind == StatusKind.Starting)
        {
            StopImminentBlink();
            UpdateAlternateButtonDisplay(AlternateButtonState.Starting);
            return;
        }

        // Neither Paused nor Starting - PowerToggleButton is the element that should be shown. Swap
        // PowerToggleAlternateButton back out instantly if it's still showing from a pause/startup grace
        // that just ended (as opposed to via Stop, which instead goes through PowerToggleButton_Unchecked
        // and never reaches this branch, since the button isn't Checked anymore by the time that runs).
        // Idempotent - a no-op on every call after the first, since _alternateButtonState is already
        // Hidden by then.
        if (_alternateButtonState != AlternateButtonState.Hidden)
        {
            ShowPowerButton(AlternateButtonState.Hidden);
        }

        if (_isPointerOverPowerButton)
        {
            StopImminentBlink(); // hovering always shows static "Stop" - it shouldn't blink.
            PowerToggleIcon.Glyph = "\uEE95"; // Stop glyph
            PowerToggleIcon.Visibility = Visibility.Visible;
            PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
            PowerToggleLabel.Text = "Stop";
            return;
        }

        if (_lastEngineStatusKind == StatusKind.JiggleStarting)
        {
            // Jiggle mode's one-shot "Starting now" has no real countdown (unlike Click mode's Starting,
            // handled above) - nothing to show on the button, so it stays plain "Stop" here, matching
            // Counter mode's behavior for this same phase and avoiding a duplicate of the same text
            // already shown on the status bar (see GetStatusBarText's _completedActionCount == 0
            // fallback to e.Text).
            StopImminentBlink(); // JiggleStarting is never StatusKind.Imminent, but be defensive.
            PowerToggleIcon.Glyph = "\uEE95"; // Stop glyph
            PowerToggleIcon.Visibility = Visibility.Visible;
            PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
            PowerToggleLabel.Text = "Stop";
            return;
        }

        // Live countdown text - can grow arbitrarily long (large hour counts, see
        // ApplyCountdownFontSize), so its font size needs to shrink to match.
        PowerToggleIcon.Visibility = Visibility.Collapsed;
        ApplyCountdownFontSize(PowerToggleLabel, _lastEngineStatusRemaining);
        PowerToggleLabel.Text = _lastEngineStatusText;

        // Last 3 seconds of this countdown: blink the text as an extra "about to fire" cue. Reverts the
        // instant the kind is next reported as Running (the action just fired and a fresh, non-Imminent
        // interval began) - see StartImminentBlinkIfNeeded/StopImminentBlink's own doc comments.
        if (_lastEngineStatusKind == StatusKind.Imminent)
        {
            StartImminentBlinkIfNeeded();
        }
        else
        {
            StopImminentBlink();
        }
    }

    /// <summary>
    /// Countdown mode's Paused-and-Starting half of UpdatePowerButtonCountdownDisplay - owns everything
    /// about PowerToggleAlternateButton (see MainWindow.xaml's comment on it for why it's a wholly
    /// separate element rather than anything layered on top of PowerToggleButton) whenever the engine's
    /// last report is StatusKind.Paused (pause-on-movement) or StatusKind.Starting (Click
    /// mode's 3-second startup grace, shown here only while Countdown mode is selected - see
    /// UpdatePowerButtonCountdownDisplay): visibility (swapped in instantly here, no animation), colors
    /// (GetAlternateButtonBackground/GetAlternateButtonForeground - see those methods' own doc comments),
    /// and text/icon content.
    ///
    /// Starting is handled first and returns immediately, deliberately bypassing the shared hover-Stop
    /// check below: unlike Paused, hovering the button during the startup countdown should keep showing
    /// the countdown, not "Stop" - the countdown is brief (max 3s) and is the entire point of this state,
    /// so hiding it behind "Stop" for the duration of a hover would read as more confusing than helpful.
    /// It never needs ApplyCountdownFontSize's large-hour-count shrink either, for the same reason
    /// Imminent's blink doesn't (a sub-4-second remaining value never reaches that tier), and never shows
    /// an icon - just _lastEngineStatusText passed straight through, the exact same text
    /// PowerToggleButton itself would otherwise be showing on the status bar (see GetStatusBarText).
    ///
    /// Paused keeps its original shape: hover-Stop first (shared across both states via
    /// _isPointerOverPowerButton, since pointer-over doesn't care which physical element the pointer
    /// actually landed on), then splits on _lastEngineStatusRemaining into plain-Paused vs
    /// Resuming-in-Xs. The foreground color is computed once up front and applied uniformly to every
    /// branch (Starting included) rather than per-branch, since GetAlternateButtonForeground wants the
    /// exact same treatment applied everywhere for a given state - see its own doc comment.
    /// </summary>
    private void UpdateAlternateButtonDisplay(AlternateButtonState state)
    {
        ShowPowerButton(state);
        ApplyAlternateButtonBackgroundBrushes(state);

        var foregroundBrush = new SolidColorBrush(GetAlternateButtonForeground(state));
        PowerToggleAlternateIcon.Foreground = foregroundBrush;
        PowerToggleAlternateLabel.Foreground = foregroundBrush;

        if (state == AlternateButtonState.Starting)
        {
            PowerToggleAlternateIcon.Visibility = Visibility.Collapsed;
            PowerToggleAlternateLabel.ClearValue(TextBlock.FontSizeProperty);
            PowerToggleAlternateLabel.Text = _lastEngineStatusText;
            return;
        }

        if (_isPointerOverPowerButton)
        {
            PowerToggleAlternateLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
            PowerToggleAlternateIcon.Glyph = "\uEE95"; // Stop glyph
            PowerToggleAlternateIcon.Visibility = Visibility.Visible;
            PowerToggleAlternateLabel.Text = "Stop";
            return;
        }

        if (_lastEngineStatusRemaining.HasValue)
        {
            // 5+ seconds of stillness have passed (see MouseAutomationEngine's StillnessDisplayThreshold) -
            // the engine now has an actual resume countdown to report, so show it directly here instead of
            // the plain "Paused" text (the status bar shows the click/jiggle count throughout the whole pause
            // instead - see GetStatusBarText). Reuses the engine's own countdown formatting/threshold logic
            // (public specifically for this) so "now" vs "in Xs" phrasing matches the rest of the app
            // exactly. Fresh movement resets this back to null (see the engine's manualMovementDetected
            // report), which naturally reverts this branch back to plain "Paused" below. Can grow
            // arbitrarily long (large hour counts, see ApplyCountdownFontSize) same as the main running
            // countdown, since it's bounded by the same configured interval.
            PowerToggleAlternateIcon.Visibility = Visibility.Collapsed;
            ApplyCountdownFontSize(PowerToggleAlternateLabel, _lastEngineStatusRemaining);
            PowerToggleAlternateLabel.Text = GetPausedStatusPhrase();
            return;
        }

        PowerToggleAlternateLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
        PowerToggleAlternateIcon.Glyph = "\uF8AE"; // PauseBold glyph
        PowerToggleAlternateIcon.Visibility = Visibility.Visible;
        PowerToggleAlternateLabel.Text = GetPausedStatusPhrase();
    }

    /// <summary>
    /// The paused-state phrase text, shared between UpdateAlternateButtonDisplay's Paused-state
    /// PowerToggleAlternateLabel (IsCountdownDisplayActive's on-button paused state) and
    /// GetStatusBarText's toggle-off base text for a Paused report (the toggle-off state has no
    /// button-side paused indicator of its own, but still needs this exact phrasing on the status bar
    /// instead of the engine's raw "Paused... Resuming in Xs" e.Text, which is phrased for Counter mode -
    /// see GetStatusBarText). Reads
    /// _lastEngineStatusRemaining exactly as UpdateAlternateButtonDisplay's Paused branch always has: once
    /// the engine's StillnessDisplayThreshold has passed and a real resume countdown is available,
    /// "Resuming in Xs"/"Resuming now" (via the engine's own FormatCountdownStatus/FormatSeconds, so the
    /// "now" vs "in Xs" split matches the rest of the app exactly); otherwise plain "Paused" for the first
    /// few seconds of stillness before that. Text only - button-specific concerns (icon glyph/visibility,
    /// ApplyCountdownFontSize) stay in UpdateAlternateButtonDisplay itself. Never called for the Starting
    /// state, which passes _lastEngineStatusText straight through instead of building its own phrase.
    /// </summary>
    private string GetPausedStatusPhrase() =>
        _lastEngineStatusRemaining.HasValue
            ? MouseAutomationEngine.FormatCountdownStatus("Resuming now", _lastEngineStatusRemaining.Value, $"Resuming in {MouseAutomationEngine.FormatSeconds(_lastEngineStatusRemaining.Value)}")
            : "Paused";

    /// <summary>
    /// Sets (or, if shrinking isn't needed, clears back to the inherited/base size) `label`'s FontSize for
    /// a countdown string - see GetCountdownFontSize for the actual sizing logic.
    /// </summary>
    private void ApplyCountdownFontSize(TextBlock label, TimeSpan? remaining)
    {
        var fontSize = GetCountdownFontSize(remaining);
        if (fontSize.HasValue)
        {
            label.FontSize = fontSize.Value;
        }
        else
        {
            label.ClearValue(TextBlock.FontSizeProperty);
        }
    }

    /// <summary>
    /// Font size for a countdown string like "Clicking in 23h 14m 06s"/"Resuming in 23h 14m 06s" (see
    /// MouseAutomationEngine.FormatSeconds's "Xh Ym Zs" format) - minutes/seconds always stay at 2 digits
    /// there, but hours can grow arbitrarily large (HoursBox's Maximum is 1,666,665), long enough to
    /// overflow PowerToggleButton/PowerToggleAlternateButton's fixed 220px width at the normal size. Steps
    /// down PowerToggleButton's own FontSize (read fresh each time rather than duplicated as a separate
    /// constant, so this always matches whatever's actually set in XAML) by
    /// PowerButtonCountdownFontStepDown once per extra digit in the hours portion, starting once it
    /// reaches double digits (10h+ is one step down, 100h+ two steps) - PowerButtonMinimumCountdownFontSize
    /// is set to exactly the 100h+ size, so shrinking effectively stops there: 1000h+, 10000h+, and beyond
    /// all render at that same size rather than continuing to shrink. Returns null - "use the base/inherited
    /// size" - whenever remaining is null or hours is still single-digit, so callers know to ClearValue
    /// instead of assigning a redundant explicit size.
    /// </summary>
    private double? GetCountdownFontSize(TimeSpan? remaining)
    {
        if (remaining is not { } value)
        {
            return null;
        }

        var hours = (int)value.TotalHours;
        if (hours < 10)
        {
            return null;
        }

        var digitCount = hours.ToString(CultureInfo.InvariantCulture).Length;
        var tier = digitCount - 1;
        var baseFontSize = PowerToggleButton.FontSize;
        return Math.Max(baseFontSize - tier * PowerButtonCountdownFontStepDown, PowerButtonMinimumCountdownFontSize);
    }

    /// <summary>
    /// Pushes a fresh color into PowerToggleAlternateNormalBrush/PointerOverBrush/PressedBrush - the three
    /// named brushes shadowing ButtonBackground/ButtonBackgroundPointerOver/ButtonBackgroundPressed inside
    /// PowerToggleAlternateButton's own Resources (see MainWindow.xaml's comment on that Button.Resources
    /// block for the full mechanism). All three get the same color - GetAlternateButtonBackground doesn't
    /// vary by pointer state (see its own doc comment) - but all three still need updating, since
    /// DefaultButtonStyle's native CommonStates Storyboard picks whichever one currently matches the
    /// button's real pointer state, and any of the three could be the one showing at a given moment.
    /// Mutates each brush's existing Color in place - never replaces the brush object or ClearValues it -
    /// so whichever one is currently showing keeps rendering, just repainted with the color this call just
    /// gave it. Called from UpdateAlternateButtonDisplay every time the button's look needs to (re)apply -
    /// e.g. a fresh pause, a fresh startup grace period, or a theme change.
    /// </summary>
    private void ApplyAlternateButtonBackgroundBrushes(AlternateButtonState state)
    {
        var background = GetAlternateButtonBackground(state);
        PowerToggleAlternateNormalBrush.Color = background;
        PowerToggleAlternatePointerOverBrush.Color = background;
        PowerToggleAlternatePressedBrush.Color = background;
    }

    // ============================================================================================
    // PowerToggleAlternateButton colorway - a translucent fill (GetAlternateButtonBackground) with tinted
    // text shaded on top (GetAlternateButtonForeground). Starting is simplest: both methods read
    // SuccessBrushSource's plain green, unadjusted. Paused is where they diverge: the background's base
    // color itself differs by theme (raw SystemAccentColor in Light, AccentFillColorDefaultBrush's
    // Windows-adjusted "brighter" Fill variant in Dark - see GetAlternateButtonBackground's own doc
    // comment for why), while the foreground always swaps in its own separately-tuned live brush
    // regardless of theme (AccentBrushSource's AccentTextFillColorPrimaryBrush) for legibility as text -
    // deliberately never matching the background's base color exactly, since text and background need to
    // stay visually distinct from each other, not collapse into the same shaded tone. SuccessBrushSource
    // in particular has to be a live, already-theme-resolved FrameworkElement's Foreground rather than a
    // raw Application.Current.Resources[] indexer lookup, since SystemFillColorSuccessBrush (unlike
    // SystemAccentColor) is Light/Dark/HighContrast ThemeDictionary-scoped, and a flat lookup for a scoped
    // key resolves against Application.RequestedTheme/ActualTheme, not RootGrid's own ActualTheme - these
    // disagree once the in-app Theme setting overrides the OS default (this app only ever sets
    // RootGrid.RequestedTheme, never Application.Current.RequestedTheme - see ApplyTheme). A real
    // FrameworkElement's ActualTheme correctly inherits from RootGrid instead, so its already-resolved
    // Foreground is always correct regardless of which theme Application itself thinks is active - the
    // same reasoning DisabledBrushSource uses elsewhere (see its own shared XAML
    // comment). Shading amounts (PausedButtonHoverLightenAmount/PausedButtonPressedDarkenAmount below -
    // shared by both states despite the "Paused" name; see those constants' own comments) apply on top of
    // the foreground's per-state base either way. An earlier "solid" colorway (a fully opaque fill, no
    // text tint in Light theme, a flat dark tint in Dark theme) was tried first for the Paused state and
    // replaced by this one - see git history on this branch if that's ever worth revisiting.
    // ============================================================================================

    /// <summary>
    /// PowerToggleAlternateButton's translucent background - the state's own base color at real
    /// PausedButtonBackgroundOpacity alpha, no opaque layer underneath at all. Starting always uses
    /// SuccessBrushSource's plain green (see the colorway comment above). Paused differs by theme: Light
    /// reads Application.Current.Resources["SystemAccentColor"] directly - pure, unadjusted accent, tried
    /// and preferred by eye over the Windows-tuned "Fill" variant here (SystemAccentColor has no Light/Dark
    /// ThemeDictionary scoping, confirmed by reading the installed WinUI SDK's generic.xaml directly, so a
    /// flat Resources[] indexer lookup is reliable for it regardless of theme) - while Dark reads
    /// AccentFillBrushSource.Foreground (ThemeResource AccentFillColorDefaultBrush), the same
    /// Windows-adjusted "brighter" Fill-purposed shade tried for both themes at one point (see git history
    /// on this branch), kept for Dark specifically once Light turned out to read better with the plain raw
    /// color instead. Either way, this color never renders at full solid strength (see the translucency
    /// below), and GetAlternateButtonForeground's text still needs its own, separately-tuned source
    /// (AccentTextFillColorPrimaryBrush) to read well on top of it regardless of which background variant
    /// is active - matching that text color exactly here was tried early on and didn't look good, since
    /// text and background need to stay visually distinct from each other, not collapse into the same
    /// shaded tone. This is genuine GPU alpha transparency, unlike a pre-blended-opaque trick tried earlier
    /// (and unlike the even earlier PowerTogglePausedOverlay design, which had to fake translucency this
    /// same way for a different reason) - both of those existed specifically because whatever sat behind
    /// the color at render time used to be unpredictable: PowerTogglePausedOverlay literally sat on top of
    /// PowerToggleButton's own real, hover/press-reactive accent background. That's no longer true -
    /// PowerToggleAlternateButton is its own independent element (see MainWindow.xaml's comment on it), so
    /// whatever's behind it at render time is just the plain window/Mica backdrop, not another element's
    /// shifting color - real transparency is safe here now. No hover/pressed shading applied to the
    /// background itself - it's the same translucent color regardless of pointer state, and doesn't vary
    /// per-VisualState either (see ApplyAlternateButtonBackgroundBrushes, which pushes this same one color
    /// into all three shadowed brushes) - that shading lives in the foreground instead (see
    /// GetAlternateButtonForeground).
    /// </summary>
    private Color GetAlternateButtonBackground(AlternateButtonState state)
    {
        var tintedColor = state == AlternateButtonState.Paused
            ? (RootGrid.ActualTheme == ElementTheme.Dark
                ? ((SolidColorBrush)AccentFillBrushSource.Foreground).Color
                : (Color)Application.Current.Resources["SystemAccentColor"])
            : ((SolidColorBrush)SuccessBrushSource.Foreground).Color;
        return Color.FromArgb((byte)Math.Round(255 * PausedButtonBackgroundOpacity), tintedColor.R, tintedColor.G, tintedColor.B);
    }

    /// <summary>
    /// PowerToggleAlternateButton's icon/label Foreground, per state - both read a live, already-theme-
    /// resolved FrameworkElement's Foreground directly (see the colorway comment above), the same shape
    /// for both: Paused reads AccentBrushSource.Foreground (ThemeResource AccentTextFillColorPrimaryBrush)
    /// - Windows' own accent-as-text shade (resolving to a lighter accent variant in Dark theme, a darker
    /// one in Light theme), tuned by the system across any accent color to read well as text against the
    /// translucent background (see GetAlternateButtonBackground), which the raw accent color at its
    /// normal strength doesn't - rather than this app trying to reproduce that per-theme tuning with its
    /// own math (a plain RGB Lighten, blend toward white, and later an HSL lightness-boost helper,
    /// Vibrant, were both tried and dropped here - see git history on this branch). Starting reads
    /// SuccessBrushSource.Foreground (ThemeResource SystemFillColorSuccessBrush) - the same green this app
    /// already uses elsewhere for its "Success" status tone (see ApplyEngineStatus's StatusTone.Success) -
    /// which already reads fine as text against the translucent fill as-is, in either theme, so no further
    /// adjustment is needed there. Applied for EVERY state shown on this button while Paused ("Paused",
    /// "Resuming in Xs", and hover-"Stop" alike - the "Stop" text deliberately does not get its own
    /// distinct color, it matches the Paused state). Hover/press shading (Lighten/Darken - plain RGB
    /// blends; small, brief interactive nudges, not the resting-state legibility fix above) is layered on
    /// top of that per-state base either way - since the fill itself (see GetAlternateButtonBackground)
    /// never shades, this is what makes hover/press feedback visible at all, for both states (even though
    /// Starting never actually swaps its text to "Stop" on hover - see UpdateAlternateButtonDisplay - the
    /// tactile lighten/darken still applies).
    /// </summary>
    private Color GetAlternateButtonForeground(AlternateButtonState state)
    {
        var textColor = state == AlternateButtonState.Paused
            ? ((SolidColorBrush)AccentBrushSource.Foreground).Color
            : ((SolidColorBrush)SuccessBrushSource.Foreground).Color;
        return _isPowerButtonPressed
            ? Darken(textColor, PausedButtonPressedDarkenAmount)
            : _isPointerOverPowerButton
                ? Lighten(textColor, PausedButtonHoverLightenAmount)
                : textColor;
    }

    /// <summary>Blends `color` toward white by `amount` (0..1) - used for PowerToggleAlternateButton's hover shade.</summary>
    private static Color Lighten(Color color, double amount) =>
        Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
            (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
            (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    /// <summary>Blends `color` toward black by `amount` (0..1) - used for PowerToggleAlternateButton's pressed shade.</summary>
    private static Color Darken(Color color, double amount) =>
        Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * (1 - amount), 0, 255),
            (byte)Math.Clamp(color.G * (1 - amount), 0, 255),
            (byte)Math.Clamp(color.B * (1 - amount), 0, 255));

    /// <summary>
    /// Standard "alpha-over" compositing formula, computed by hand and returned fully opaque (A=255) -
    /// simulates translucency as a flat color instead of relying on real GPU alpha blending, for whenever
    /// what's actually behind an element at render time is unpredictable (see MainWindow.xaml's comment on
    /// PowerToggleAlternateButton for the real bug this solved once). Currently unused -
    /// GetAlternateButtonBackground uses genuine alpha transparency instead, now that
    /// PowerToggleAlternateButton is a wholly independent element with a stable, predictable backdrop
    /// behind it - kept here in case a future need for this trick comes up again for some other
    /// unpredictable-backdrop case.
    /// </summary>
    private static Color BlendOver(Color background, Color foreground, double foregroundOpacity) =>
        Color.FromArgb(
            255,
            (byte)Math.Round(background.R * (1 - foregroundOpacity) + foreground.R * foregroundOpacity),
            (byte)Math.Round(background.G * (1 - foregroundOpacity) + foreground.G * foregroundOpacity),
            (byte)Math.Round(background.B * (1 - foregroundOpacity) + foreground.B * foregroundOpacity));

    /// <summary>
    /// Formats a completed-action count as "{count} click"/"{count} clicks" (Click mode) or
    /// "{count} jiggle"/"{count} jiggles" (Jiggle mode), singular only when count == 1.
    /// </summary>
    private static string FormatActionCount(int count, AutomationMode mode)
    {
        var noun = mode == AutomationMode.Click ? "click" : "jiggle";
        return count == 1 ? $"{count} {noun}" : $"{count} {noun}s";
    }

    /// <summary>
    /// Reverts StatusTextBlock back to plain "Off" styling once the user reconfigures automation
    /// (mode, interval, pause-on-movement, scheduled stop) after a run has ended - so a lingering
    /// "Stopped after N clicks" result doesn't stay attached to settings that no longer describe it.
    /// Guarded to never stomp on a live status while the engine is actually running, though the
    /// controls that call this are already disabled during a run so that shouldn't normally happen.
    /// </summary>
    private void ResetStatusToOffIfNotRunning()
    {
        if (_engine.IsRunning)
        {
            return;
        }

        SetStatusText("Off", StatusTone.Muted);
    }

    // How long Jiggle mode's one-shot "Starting now" (StatusKind.JiggleStarting) stays on screen before
    // later status reports are allowed to overwrite it - purely cosmetic (so it doesn't flash by
    // faster than a human can read it), independent of MouseAutomationEngine's actual interval
    // timer, which fires the next jiggle on its own schedule regardless of this hold.
    private static readonly TimeSpan JiggleStartingStatusHoldDuration = TimeSpan.FromMilliseconds(500);
    private bool _statusHoldActive;
    private StatusChangedEventArgs? _pendingStatusAfterHold;

    // How much PowerToggleAlternateButton's current per-state text color (see GetAlternateButtonForeground)
    // is blended toward white/black for its hover/pressed shades (see Lighten/Darken) - gives
    // PowerToggleAlternateButton the same three-tier tactile
    // feedback the native accent PowerToggleButton already gets for free from
    // Checked/CheckedPointerOver/CheckedPressed, for both its Paused and Starting states. Tuned by eye
    // against the real button: light enough to read as a clearly different shade, not so strong it looks
    // like a different hue.
    private const double PausedButtonHoverLightenAmount = 0.12;
    private const double PausedButtonPressedDarkenAmount = 0.18;

    // GetCountdownFontSize's step size (points knocked off per extra hours digit) and floor. The floor is
    // deliberately set to exactly the size the 100h+ tier (3 digits) already lands on - 18 - 2*2 - rather
    // than a smaller legibility minimum, so the shrinking effectively stops at 100h+: that size read fine
    // for every larger hour count tried, no reason to keep shrinking past it for 1000h/10000h/etc.
    private const double PowerButtonCountdownFontStepDown = 2;
    private const double PowerButtonMinimumCountdownFontSize = 14;

    // Real alpha (0..1, fed straight into a Color's A channel) for GetAlternateButtonBackground, so the
    // fill reads as a faint, translucent tint rather than a bold solid block, for both states. Same value
    // both themes - a Dark-only 0.2 was tried early on and tuned back up to match, in favor of
    // brightening the Dark-theme base color itself instead (this attempt predates the Starting state, and
    // was reverted at the time; the idea was retried later, first as a hand-rolled HSL lightness boost,
    // then as AccentFillColorDefaultBrush - kept for Dark theme specifically once Light turned out to read
    // better with the plain raw accent color instead - see GetAlternateButtonBackground's own comment).
    private const double PausedButtonBackgroundOpacity = 0.3;

    // StartImminentBlinkIfNeeded's fade range/speed for PowerToggleLabel's opacity during the last 3
    // seconds of a regular per-action countdown (StatusKind.Imminent). Duration is one direction of the
    // AutoReverse fade (down, or back up), so a full dim-then-brighten cycle takes twice this - tuned to
    // give roughly six full pulses across the 3-second Imminent window, brisk enough to read as an urgent
    // "about to fire" cue rather than a lazy pulse. ImminentBlinkMinOpacity of 0.25 (not lower) keeps the
    // countdown text still faintly legible mid-fade, never fully invisible.
    private const double ImminentBlinkMinOpacity = 0.25;
    private static readonly TimeSpan ImminentBlinkHalfCycleDuration = TimeSpan.FromSeconds(0.25);

    // StartImminentBlinkIfNeeded suppresses the blink entirely (leaving the countdown text at a
    // constant, unblinking full opacity) when _runningInterval is below this - on a short interval the
    // 3-second Imminent window covers a large fraction of every single cycle, so the blink would be
    // running almost constantly rather than reading as a distinct "about to fire" cue.
    private static readonly TimeSpan ImminentBlinkMinimumInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Which of PowerToggleButton/PowerToggleAlternateButton is currently shown, and - when it's the
    /// latter - which of its two states it's showing: pause-on-movement (accent-colored) or
    /// Click mode's 3-second startup grace period (green - see UpdateAlternateButtonDisplay), the latter
    /// only while "Stop button display" is set to Countdown (see UpdatePowerButtonCountdownDisplay).
    /// Replaces what used to be a plain bool (_isPausedButtonShowing) back when
    /// PowerToggleAlternateButton had only one purpose - now that it represents two mutually-exclusive
    /// states, a bool can no longer distinguish "hidden" from "which state is showing," hence the enum.
    /// </summary>
    private enum AlternateButtonState
    {
        Hidden,
        Paused,
        Starting
    }

    // Mirrors which of PowerToggleButton/PowerToggleAlternateButton ShowPowerButton last made the "shown"
    // one (and which state, if PowerToggleAlternateButton) - kept as a plain field rather than
    // re-checking .Visibility everywhere, mostly for readability.
    private AlternateButtonState _alternateButtonState;

    /// <summary>
    /// Shows exactly one of PowerToggleButton/PowerToggleAlternateButton (Visibility.Visible) and hides the
    /// other (Visibility.Collapsed) - the only place either element's shown/hidden state is ever touched.
    /// (An Opacity/IsHitTestVisible-based version was also tried, to see if keeping both elements
    /// permanently in the visual tree would fix the stop-while-paused accent flash - it didn't, see git
    /// history on this branch.)
    /// </summary>
    private void ShowPowerButton(AlternateButtonState state)
    {
        _alternateButtonState = state;
        PowerToggleButton.Visibility = state == AlternateButtonState.Hidden ? Visibility.Visible : Visibility.Collapsed;
        PowerToggleAlternateButton.Visibility = state == AlternateButtonState.Hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    // Non-null exactly while the Imminent blink (see StartImminentBlinkIfNeeded/StopImminentBlink) is
    // actually running - lets StartImminentBlinkIfNeeded no-op on every call after the first instead of
    // restarting the animation's phase on every ~100ms status tick, which would turn a smooth pulse into
    // a stutter.
    private Storyboard? _imminentBlinkStoryboard;

    /// <summary>
    /// Starts (once - see _imminentBlinkStoryboard) a repeating fade between full opacity and
    /// ImminentBlinkMinOpacity on PowerToggleLabel, called only from UpdatePowerButtonCountdownDisplay's
    /// live-countdown branch while the engine's last report is StatusKind.Imminent (the last 3 seconds of
    /// a regular per-action countdown - never pause-on-movement or Click mode's startup grace,
    /// which are StatusKind.Paused/StatusKind.Starting and handled entirely separately by
    /// PowerToggleAlternateButton). Only touches Opacity, deliberately - unlike Background/Foreground,
    /// PowerToggleButton's native ControlTemplate never animates that property via its own CommonStates
    /// Storyboards, so this can't fight the native Checked-state rendering the way recoloring the button's
    /// own background from code would (the exact problem PowerToggleAlternateButton
    /// exists to route around entirely, for a different property).
    ///
    /// No-ops (leaving PowerToggleLabel at full, unblinking opacity) below ImminentBlinkMinimumInterval -
    /// see that constant's own comment. This check lives here rather than at the call site so it's
    /// impossible to start the blink from anywhere without it applying.
    /// </summary>
    private void StartImminentBlinkIfNeeded()
    {
        if (_imminentBlinkStoryboard is not null || _runningInterval < ImminentBlinkMinimumInterval)
        {
            return;
        }

        var fade = new DoubleAnimation
        {
            To = ImminentBlinkMinOpacity,
            Duration = ImminentBlinkHalfCycleDuration,
            AutoReverse = true
        };
        Storyboard.SetTarget(fade, PowerToggleLabel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        storyboard.Children.Add(fade);
        storyboard.Begin();
        _imminentBlinkStoryboard = storyboard;
    }

    /// <summary>
    /// Stops the Imminent blink (safe/idempotent - a no-op if it isn't currently running) and always
    /// explicitly resets PowerToggleLabel.Opacity back to 1, since Storyboard.Stop() leaves the animated
    /// property wherever the fade happened to be mid-cycle rather than snapping it back on its own.
    /// Called from every branch that could otherwise leave a stale blink running under text it was never
    /// meant to apply to: UpdatePowerButtonCountdownDisplay's Paused/hover/Starting-JiggleStarting branches
    /// and its own live-countdown branch once the kind is no longer Imminent, UpdatePowerButtonRunningDisplay's
    /// Counter-mode path and UpdatePowerButtonNoneDisplay (in case "Stop button display" is switched away
    /// from Countdown mid-blink), and PowerToggleButton_Checked/_Unchecked as a fresh-run/stop safety net.
    /// </summary>
    private void StopImminentBlink()
    {
        _imminentBlinkStoryboard?.Stop();
        _imminentBlinkStoryboard = null;
        PowerToggleLabel.Opacity = 1;
    }

    private void Engine_StatusChanged(object? sender, StatusChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_statusHoldActive)
            {
                _pendingStatusAfterHold = e;
                return;
            }

            ApplyEngineStatus(e);

            if (e.Kind == StatusKind.JiggleStarting)
            {
                _statusHoldActive = true;
                _pendingStatusAfterHold = null;
                _ = ReleaseStatusHoldAfterDelayAsync();
            }
        });
    }

    private async Task ReleaseStatusHoldAfterDelayAsync()
    {
        await Task.Delay(JiggleStartingStatusHoldDuration);

        DispatcherQueue.TryEnqueue(() =>
        {
            _statusHoldActive = false;
            if (_pendingStatusAfterHold is { } pending)
            {
                _pendingStatusAfterHold = null;
                ApplyEngineStatus(pending);
            }
        });
    }

    private void ApplyEngineStatus(StatusChangedEventArgs e)
    {
        // Guards against a race in the countdown-heavy paths (Click mode's startup grace,
        // pause-on-movement's resume countdown): the engine's background loop reports the
        // current tick (ReportStatus -> StatusChanged -> DispatcherQueue.TryEnqueue) and only then
        // awaits its next Task.Delay - so a Stop() that lands on the UI thread during that delay
        // (e.g. PowerToggleButton_Unchecked, fired here by the tray's "Stop" item just as easily as
        // a real button click) can flip _engine.IsRunning false and set the "Off"/counter status
        // *before* that already-queued callback gets its turn to run. Without this check, the stale
        // callback would still fire afterward and stomp the just-set "Off" text with the countdown
        // text it captured (and would also re-mark the tray icon as running via the
        // isRunning: true below). Same guard protects the ReleaseStatusHoldAfterDelayAsync ->
        // ApplyEngineStatus(pending) call site.
        if (!_engine.IsRunning)
        {
            return;
        }

        _lastEngineStatusKind = e.Kind;
        _lastEngineStatusText = e.Text;
        _lastEngineStatusRemaining = e.Remaining;

        // StatusKind.Paused (Caution/yellow) and StatusKind.Imminent (Critical/red) only get their
        // attention-grabbing tones when the status bar text is itself the countdown/pause indicator -
        // that's Counter mode (always) and the toggle-off state (always, since the button never shows
        // anything but plain "Stop" - see UpdatePowerButtonNoneDisplay - so the status bar is the ONLY
        // place either state is visible at all). IsCountdownDisplayActive is the one exception: its Stop
        // button already carries both of those cues itself (accent-colored background while paused - see
        // GetAlternateButtonBackground - and live countdown text as an action nears - see
        // UpdatePowerButtonCountdownDisplay), and the status bar there is instead just showing the running
        // click/jiggle count (see GetStatusBarText) - a count has no "paused" or "about to fire" meaning of
        // its own, so tinting it Caution/Critical there would be a redundant, confusing second indicator of
        // the same state already shown on the button. It stays the regular Muted tone there for both, hence
        // the guards below excluding only IsCountdownDisplayActive rather than requiring Counter/toggle-off
        // specifically.
        //
        // StatusKind.Starting is the same story, one level down: while IsCountdownDisplayActive
        // specifically, Click mode's 3-second startup grace now shows its own green countdown on
        // PowerToggleAlternateButton (see UpdatePowerButtonCountdownDisplay/UpdateAlternateButtonDisplay)
        // instead of the plain "Stop" it used to - the status bar's own copy of that same text (see
        // GetStatusBarText) would be a literal duplicate, so it falls back to plain Muted "Off" there
        // instead of Success/green. Every other case - and JiggleStarting always, since it has no on-button
        // countdown of its own to hand this off to - keeps the original Success tone.
        var tone = e.Kind switch
        {
            StatusKind.Starting when IsCountdownDisplayActive => StatusTone.Muted,
            StatusKind.Starting => StatusTone.Success,
            StatusKind.JiggleStarting => StatusTone.Success,
            StatusKind.Imminent when !IsCountdownDisplayActive => StatusTone.Critical,
            StatusKind.Paused when !IsCountdownDisplayActive => StatusTone.Caution,
            _ => StatusTone.Muted
        };
        SetStatusText(GetStatusBarText(e), tone);
        UpdateTaskbarProgress(e);
        _trayIconService.UpdateState(isRunning: true, isPaused: e.Kind == StatusKind.Paused, mode: _runningMode);
        UpdatePowerButtonRunningDisplay();
    }

    /// <summary>
    /// In Counter display mode, the status bar always shows the engine's own text verbatim - unchanged
    /// from before Countdown mode existed. While IsCountdownDisplayActive, once the first action has
    /// fired (_completedActionCount > 0 - before that, e.g. during the startup grace countdown, there's no
    /// count yet, so this still falls back to the engine's own text, EXCEPT for Click mode's own startup
    /// grace - see the _completedActionCount == 0 branch below), the status bar always shows just the
    /// running click/jiggle count, e.g. "23 jiggles" - including throughout a pause. The pause-
    /// resume countdown itself lives on the button instead once it becomes available (see
    /// UpdatePowerButtonCountdownDisplay), not here, so there's no "...Resuming in Xs" suffix to build.
    ///
    /// IsCountdownDisplayActive's Click-mode startup grace (StatusKind.Starting) is a second, earlier
    /// exception to the "count == 0 falls back to e.Text" rule: since PowerToggleAlternateButton now shows
    /// this exact countdown itself (see UpdatePowerButtonCountdownDisplay/UpdateAlternateButtonDisplay),
    /// repeating it on the status bar too would be a literal duplicate of the same timer in two places at
    /// once - so this falls back to plain "Off" instead (see ApplyEngineStatus's matching Muted-tone
    /// guard). Every other case, and JiggleStarting always (it has no on-button countdown of its own to
    /// hand this off to), keeps showing e.Text as before.
    ///
    /// While ShowStopButtonDisplayToggle is off (PowerToggleButton always just shows plain "Stop" - see
    /// UpdatePowerButtonNoneDisplay - so this is the ONLY place either the countdown or the count is ever
    /// visible), once the first action has fired the status bar combines both on one line, separated by
    /// " \u00B7 " (a space, U+00B7 MIDDLE DOT, a space) - e.g. "Jiggling in 3m 14s \u00B7 23 jiggles" or
    /// "Resuming in 12s \u00B7 76 jiggles". The base phrase before the separator is e.Text verbatim for every
    /// StatusKind except Paused: e.Text for a Paused report is phrased for Counter mode specifically (built
    /// by the engine's own RunLoopAsync as "Paused... Resuming in Xs"), which would read as a redundant
    /// "Paused... Resuming in 12s \u00B7 76 jiggles" here - GetPausedStatusPhrase (the same helper
    /// UpdateAlternateButtonDisplay's on-button Paused text already uses) gives the clean "Resuming in
    /// 12s"/"Paused" phrasing this state actually needs instead.
    /// </summary>
    private string GetStatusBarText(StatusChangedEventArgs e)
    {
        if (_completedActionCount == 0)
        {
            return e.Kind == StatusKind.Starting && IsCountdownDisplayActive
                ? "Off"
                : e.Text;
        }

        if (!_settingsPanel.IsStopButtonDisplayShown)
        {
            return $"{(e.Kind == StatusKind.Paused ? GetPausedStatusPhrase() : e.Text)} \u00B7 {FormatActionCount(_completedActionCount, _runningMode)}";
        }

        return IsCountdownDisplayActive ? FormatActionCount(_completedActionCount, _runningMode) : e.Text;
    }

    /// <summary>
    /// Mirrors the engine's countdown onto the taskbar icon's progress bar (see
    /// Services/TaskbarProgressService) when ShowTaskbarProgressToggle is on - no-ops entirely
    /// otherwise, which is also why turning the setting off mid-run needs its own explicit Clear()
    /// call (see ShowTaskbarProgressToggle_Toggled). e.Progress is null for a "Paused" report that
    /// doesn't carry its own countdown (movement just detected, before the resume countdown appears) -
    /// _lastTaskbarProgress keeps the bar at wherever it already was instead of snapping to 0.
    /// </summary>
    private void UpdateTaskbarProgress(StatusChangedEventArgs e)
    {
        if (!_showTaskbarProgress)
        {
            return;
        }

        if (e.Progress.HasValue)
        {
            _lastTaskbarProgress = e.Progress.Value;
        }

        _taskbarProgressService.SetProgress(_lastTaskbarProgress, paused: e.Kind == StatusKind.Paused);
    }

    private void Engine_AutoStopped(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (PowerToggleButton.IsChecked == true)
            {
                // See the comment on _isProgrammaticToggleOff's declaration - this stop sets IsChecked
                // directly, with no click involved, so it must be excluded from the shrug-status check.
                _isProgrammaticToggleOff = true;

                // Setting this raises Unchecked, which stops the engine and resets the rest of the UI.
                PowerToggleButton.IsChecked = false;
            }
        });
    }

    /// <summary>
    /// Single handling point for the Auto click / Jiggle mode switch's actual selection: fires whenever
    /// ModeSegmentedControl's selection changes, whether that's the user tapping a segment directly or
    /// SetSelectedMode forcing one programmatically (see that method - used by the tray context menu's
    /// "Start Auto Click"/"Start Jiggle" items, which need to force one specific mode rather than
    /// toggle it). Skips everything below while _isSyncingModeSelection is set (see that field's own
    /// comment), so UpdateModeIndicators' one-way visual sync of SelectedIndex from _isJiggleModeSelected
    /// never replays these side effects for a selection change that didn't actually originate from the
    /// user or the tray.
    ///
    /// Updates _isJiggleModeSelected - the real source of truth the rest of the app reads via
    /// CurrentSelectedMode - plays whichever icon's wiggle/spin animation just became selected, resets
    /// the status text back to Off if the engine isn't already running, refreshes the auto-stop
    /// button's "after N clicks/jiggles" label (which must track whichever mode is now selected, even if
    /// the user never reopens AutoStopDialog after switching), persists the choice as LastMode so the
    /// app reopens in the same mode next launch, and keeps the tray tooltip's mode name live even while
    /// inactive (mode selection can change with no Start/Stop in between - see TrayIconService.
    /// UpdateState's other call sites). Finishes with a call to UpdateModeIndicators() - SelectedIndex
    /// is already correct at this point, so that call's own sync is a no-op here; it exists so every
    /// path that can change _isJiggleModeSelected runs through the same one refresh point.
    /// </summary>
    private void ModeSegmentedControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingModeSelection)
        {
            return;
        }

        _isJiggleModeSelected = ModeSegmentedControl.SelectedIndex == 1;

        (_isJiggleModeSelected ? JiggleModeIconWiggleStoryboard : ClickModeIconWiggleStoryboard).Begin();
        ResetStatusToOffIfNotRunning();
        UpdateAutoStopButtonLabel();
        ConfigService.Update(c => c.LastMode = _isJiggleModeSelected ? "Jiggle" : "Click");
        _trayIconService.UpdateState(isRunning: _engine.IsRunning, isPaused: false, mode: CurrentSelectedMode);

        UpdateModeIndicators();
    }

    /// <summary>
    /// Sets the selected Auto Click/Jiggle mode to a specific value, as opposed to a direct segment tap
    /// (which sets ModeSegmentedControl.SelectedIndex itself, straight from the control) - shared by
    /// the tray context menu's "Start Auto Click"/"Start Jiggle" items (see
    /// TrayIconService_StartRequested), which need to force one specific mode rather than toggle it.
    /// No-ops if the requested mode is already selected, so a tray-driven start in the mode that's
    /// already selected doesn't replay the switch animation/effects for nothing. Setting SelectedIndex
    /// here fires ModeSegmentedControl_SelectionChanged for real (as opposed to UpdateModeIndicators'
    /// guarded sync) - that handler is the single place the actual mode-switch side effects live, so a
    /// tray-triggered change and a direct segment tap end up running identical logic with nothing
    /// duplicated between the two entry points.
    /// </summary>
    private void SetSelectedMode(AutomationMode mode)
    {
        var isJiggle = mode == AutomationMode.Jiggle;
        if (_isJiggleModeSelected == isJiggle)
        {
            return;
        }

        ModeSegmentedControl.SelectedIndex = isJiggle ? 1 : 0;
    }

    /// <summary>
    /// Syncs ModeSegmentedControl's selection to _isJiggleModeSelected - the one piece of UI that still
    /// needs a manual refresh now that Segmented handles its own selected/unselected/disabled visuals
    /// natively (unlike the old ModeSwitchButton's hand-rolled box coloring, which this used to also
    /// drive - see ApplySelectedModeBoxStyle in git history on this branch if that's ever worth
    /// revisiting; a title bar subtitle used to be synced here too, removed along with
    /// ModeSubtitleTextBlock - see AppTitleBarGrid's own comment in MainWindow.xaml). The SelectedIndex
    /// assignment is wrapped in _isSyncingModeSelection so it never replays
    /// ModeSegmentedControl_SelectionChanged's side effects (icon animation, status/auto-stop refresh,
    /// persistence, tray update) for what's just a one-way visual sync, not an actual user- or
    /// tray-driven mode change - see that field's own comment.
    /// </summary>
    private void UpdateModeIndicators()
    {
        _isSyncingModeSelection = true;
        ModeSegmentedControl.SelectedIndex = _isJiggleModeSelected ? 1 : 0;
        _isSyncingModeSelection = false;
    }

    /// <summary>
    /// Syncs _isRandomizeIntervalEnabled from RandomizeIntervalButton.IsChecked (the real source of
    /// truth now that this is a ToggleButton) and refreshes the button's own indicator.
    /// RandomizeIntervalButton is locked (IsEnabled=false) via SetInputsEnabled while automation is
    /// running, same as MinutesBox/SecondsBox/ModeSegmentedControl - so this can only ever fire while
    /// idle, and the setting can't change out from under an in-progress run.
    /// </summary>
    private void RandomizeIntervalButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _isRandomizeIntervalEnabled = RandomizeIntervalButton.IsChecked == true;
        UpdateRandomizeIntervalIndicator();
    }

    /// <summary>
    /// Sets RandomizeIntervalIcon's Foreground and Opacity to match _isRandomizeIntervalEnabled and
    /// RandomizeIntervalButton.IsEnabled. Foreground is an explicit local value for three of the four
    /// states - otherwise it'd be left to inherit from the ContentPresenter, whose Foreground the
    /// default ToggleButton ControlTemplate re-targets per VisualState - but Checked AND locked is a
    /// deliberate exception: see that state's own bullet below for why it's ClearValue'd instead, the
    /// only state left with no customization on the icon at all (the button's border is the sole
    /// remaining customization for that state - see MainWindow.xaml's comment on
    /// ToggleButtonBorderBrushCheckedDisabled).
    ///
    /// Four states:
    ///   - Idle (Unchecked, unlocked): Foreground = PrimaryBrushSource (TextFillColorPrimaryBrush,
    ///     matching IntervalCaptionTextBlock's own undimmed base color - white in Dark theme, black in
    ///     Light theme), Opacity = 0.8. Needs to stay clearly brighter than the "INTERVAL" caption
    ///     beside it (IntervalCaptionTextBlock, which sits at a constant Opacity = 0.6 - see
    ///     IntervalCaptionLabelStyle) so the icon still visibly reads as an interactive button/toggle,
    ///     not a plain label.
    ///   - Checked AND unlocked (sitting on the accent-filled pill): Foreground = OnAccentBrushSource
    ///     (TextOnAccentFillColorPrimaryBrush - the same brush the template itself uses for
    ///     ToggleButtonForegroundChecked: black in Dark theme, white in Light theme), Opacity = 1.
    ///     This is the state where the icon sits on the fully-opaque, unlocked accent-filled pill, so
    ///     it gets the accent-contrast color at full opacity.
    ///   - Checked AND locked (automation running with randomize-interval On): Foreground is
    ///     ClearValue'd (not assigned at all), Opacity = 1. RandomizeIntervalButton.Resources no longer
    ///     overrides ToggleButtonBackgroundCheckedDisabled (see MainWindow.xaml) - Checked+Disabled's
    ///     fill is the plain default ToggleButtonStyle look now, and the default ControlTemplate's own
    ///     CheckedDisabled VisualState already re-targets ContentPresenter.Foreground to
    ///     ToggleButtonForegroundCheckedDisabled to match that exact fill - so clearing the icon's local
    ///     Foreground lets it inherit that already-correct default instead of this method re-deriving
    ///     its own color choice for a state it no longer customizes the fill of. Only
    ///     ToggleButtonBorderBrushCheckedDisabled stays a deliberate customization for this state
    ///     (unrelated to icon Foreground/Opacity, both left at default/undimmed here).
    ///   - Unchecked AND locked: Foreground = DisabledBrushSource (TextFillColorDisabledBrush),
    ///     Opacity = 0.6 - the exact same Foreground/Opacity combination IntervalCaptionLabelStyle's
    ///     own Disabled VisualState uses for the "INTERVAL" caption beside it. This state's chrome
    ///     stays fully Transparent (ToggleButtonBackgroundDisabled/ToggleButtonBorderBrushDisabled in
    ///     RandomizeIntervalButton.Resources), so there's no filled pill for an accent-contrast color
    ///     to sit on, and the icon instead dims to match the caption's own disabled look.
    ///
    /// Also updates the button's AutomationProperties.Name/ToolTip so screen readers and tooltips
    /// announce the current state, not just "Randomize interval" with no indication of on/off.
    /// </summary>
    private void UpdateRandomizeIntervalIndicator()
    {
        bool isLocked = !RandomizeIntervalButton.IsEnabled;
        bool isCheckedAndUnlocked = !isLocked && _isRandomizeIntervalEnabled;
        bool isCheckedAndLocked = isLocked && _isRandomizeIntervalEnabled;

        if (isCheckedAndLocked)
        {
            RandomizeIntervalIcon.ClearValue(FontIcon.ForegroundProperty);
        }
        else
        {
            RandomizeIntervalIcon.Foreground = isCheckedAndUnlocked
                ? OnAccentBrushSource.Foreground
                : isLocked
                    ? DisabledBrushSource.Foreground
                    : PrimaryBrushSource.Foreground;
        }
        RandomizeIntervalIcon.Opacity = isCheckedAndUnlocked || isCheckedAndLocked ? 1 : isLocked ? 0.6 : 0.8;

        AutomationProperties.SetName(
            RandomizeIntervalButton,
            _isRandomizeIntervalEnabled ? "Randomize interval, On" : "Randomize interval, Off");
        ToolTipService.SetToolTip(
            RandomizeIntervalButton,
            _isRandomizeIntervalEnabled ? "Randomize interval: On" : "Randomize interval: Off");
    }

    /// <summary>
    /// Applies _settingsPanel.ShowAdvancedIntervalDisplay - SettingsPanel's own persisted "Interval
    /// display" dropdown, the sole source of truth now that AdvancedIntervalDisplayButton
    /// has been removed from the main window entirely (see the header row's own comment in
    /// MainWindow.xaml) - by syncing _isAdvancedIntervalDisplayEnabled, swapping
    /// BasicIntervalRow/AdvancedIntervalRow's Visibility, and - only when switching TO Advanced -
    /// populating the four Hours/Minutes/Seconds/Milliseconds fields from MinutesBox.Value/
    /// SecondsBox.Value (see PopulateAdvancedIntervalFieldsFromBasic). No equivalent population runs
    /// when switching back to Basic: MinutesBox.Value/SecondsBox.Value are kept continuously up to
    /// date by AdvancedIntervalInputBox_TextChanged the entire time Advanced is showing, not just at
    /// the moment of switching away from it, so they're already correct.
    ///
    /// Called once at startup from LoadConfigIntoUi (after MinutesBox/SecondsBox are loaded) and again
    /// every time SettingsPanel.ShowAdvancedIntervalDisplayChanged fires. Persistence itself already
    /// happens in SettingsPanel.IntervalDisplayRadioButton_Checked, so this method doesn't
    /// duplicate it - unlike the old AdvancedIntervalDisplayButton_CheckedChanged this replaces, there's
    /// no second UI control left to keep in sync with, so no _isInitializing guard is needed either.
    /// </summary>
    private void UpdateAdvancedIntervalDisplayMode()
    {
        _isAdvancedIntervalDisplayEnabled = _settingsPanel.ShowAdvancedIntervalDisplay;

        BasicIntervalRow.Visibility = _isAdvancedIntervalDisplayEnabled ? Visibility.Collapsed : Visibility.Visible;
        AdvancedIntervalRow.Visibility = _isAdvancedIntervalDisplayEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (_isAdvancedIntervalDisplayEnabled)
        {
            PopulateAdvancedIntervalFieldsFromBasic();

            // Deferred one dispatcher tick: AdvancedIntervalRow's four NumberBoxes haven't gone through
            // their own first real Arrange pass yet at the point Visibility flips above (that pass
            // happens as part of the layout this triggers, not synchronously here) - each box's glyph
            // layout can be left stuck invisible if it's refreshed before that pass instead of after
            // it. See RefreshStaleNumberBoxTextLayout's own doc comment, and AutoStopDialog.Opened's
            // subscription (constructor) for the equivalent fix on that dialog.
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshStaleNumberBoxTextLayout(HoursBox);
                RefreshStaleNumberBoxTextLayout(AdvancedMinutesBox);
                RefreshStaleNumberBoxTextLayout(AdvancedSecondsBox);
                RefreshStaleNumberBoxTextLayout(MillisecondsBox);
            });
        }
    }

    /// <summary>
    /// Basic -> Advanced conversion (see UpdateAdvancedIntervalDisplayMode): converts
    /// MinutesBox.Value/SecondsBox.Value into whole Hours/Minutes/Seconds/Milliseconds via an
    /// all-integer-milliseconds intermediate, so there's no floating-point drift (e.g. Minutes=60,
    /// Seconds=30 -> totalMs=3,630,000 -> 1h 0m 30s 0ms exactly). Guarded by
    /// _isSyncingAdvancedIntervalFields so the ValueChanged/inner-TextChanged handlers these four
    /// Value writes fire (AdvancedIntervalBox_ValueChanged and AdvancedIntervalInputBox_TextChanged)
    /// don't immediately try to convert back and overwrite MinutesBox/SecondsBox with a transient,
    /// partially-populated total.
    /// </summary>
    private void PopulateAdvancedIntervalFieldsFromBasic()
    {
        var minutes = double.IsNaN(MinutesBox.Value) ? 0 : MinutesBox.Value;
        var seconds = double.IsNaN(SecondsBox.Value) ? 0 : SecondsBox.Value;
        var totalMs = (long)Math.Round((minutes * 60 + seconds) * 1000);

        var hours = totalMs / 3_600_000;
        var mins = totalMs % 3_600_000 / 60_000;
        var secs = totalMs % 60_000 / 1000;
        var millis = totalMs % 1000;

        _isSyncingAdvancedIntervalFields = true;
        HoursBox.Value = hours;
        AdvancedMinutesBox.Value = mins;
        AdvancedSecondsBox.Value = secs;
        MillisecondsBox.Value = millis;
        _isSyncingAdvancedIntervalFields = false;
    }

    /// <summary>
    /// Sets up the four Advanced interval NumberBoxes' live-sync/MaxLength/clear-button-suppression/
    /// blank-coercion hooks (see HookAdvancedIntervalBoxLiveSync for the mechanism and why), then does
    /// the equivalent MaxLength/clear-button/blank-coercion setup for Basic's MinutesBox/SecondsBox,
    /// which don't need the live-sync part since they're already the canonical source. Called once from
    /// the constructor, after InitializeComponent/LoadConfigIntoUi.
    /// </summary>
    private void InitializeAdvancedIntervalLiveSync()
    {
        HookAdvancedIntervalBoxLiveSync(HoursBox, maxLength: 7);
        HookAdvancedIntervalBoxLiveSync(AdvancedMinutesBox, maxLength: 2);
        HookAdvancedIntervalBoxLiveSync(AdvancedSecondsBox, maxLength: 2);
        HookAdvancedIntervalBoxLiveSync(MillisecondsBox, maxLength: 3);

        // Basic's MinutesBox/SecondsBox don't need the live-sync wiring above (they're already the
        // canonical source, not derived from anything else), but they still need their own MaxLength cap
        // - 8 for Minutes (matching its Maximum's own digit count, "99999959") and 6 for Seconds
        // (matching "59.999", decimal point included) - and share the same inner TextBox template as the
        // four Advanced fields, so they get the same clear-button restyling and blank-to-zero coercion
        // too (see StyleClearButton/HookBlankCoercion).
        SetInputBoxMaxLength(MinutesBox, maxLength: 8, allowDecimalPoint: false);
        SetInputBoxMaxLength(SecondsBox, maxLength: 6, allowDecimalPoint: true);
        StyleClearButton(MinutesBox);
        StyleClearButton(SecondsBox);
        HookBlankCoercion(MinutesBox);
        HookBlankCoercion(SecondsBox);
    }

    private static void SetInputBoxMaxLength(NumberBox box, int maxLength, bool allowDecimalPoint)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => SetInputBoxMaxLength(box, maxLength, allowDecimalPoint);
            return;
        }

        inputBox.MaxLength = maxLength;
        HookIntervalCharacterFilter(inputBox, allowDecimalPoint);
    }

    /// <summary>
    /// Restricts one NumberBox's inner InputBox to digits 0-9 as characters are actually typed - used
    /// for all six interval fields plus AutoStopCountBox (the "clicks/jiggles" count field in
    /// AutoStopDialog) - and, only when allowDecimalPoint is true (SecondsBox alone, the one field of
    /// the six interval ones that supports fractional values - see BasicIntervalRow's own XAML
    /// comment), a single '.'. NumberBox's
    /// own Minimum/Maximum/NumberFormatter only affect clamping/display at commit time (confirmed
    /// empirically elsewhere in this file - see IntervalBox_ValueChanged/AdvancedIntervalBox_ValueChanged's
    /// doc comments) and do nothing to stop arbitrary characters from being typed into the live Text in
    /// the first place.
    ///
    /// TextBox.BeforeTextChanging is the idiomatic hook for this and was confirmed empirically (not
    /// assumed) to fire here, synchronously, before the proposed text is applied/rendered - both for
    /// hardware-keyboard typing and for the field's own programmatic Text assignments elsewhere in this
    /// file (HookBlankCoercion's/AdvancedIntervalInputBox_TextChanged's "0" snap-back, StyleClearButton's
    /// clear-text button) - unlike PreviewKeyDown/KeyDown, which don't reliably catch paste or IME
    /// composition. Setting args.Cancel = true rejects the whole pending change and leaves Text exactly
    /// as it was, so a rejected keystroke (a letter, symbol, '-', or a second '.') never partially applies.
    ///
    /// Empty text always passes through unfiltered (args.NewText == "") so Backspace/Delete/
    /// Ctrl+A+Delete can still clear the field down to blank - the various blank-to-"0" coercions run
    /// afterward, in their own TextChanged handlers, and that "0" is itself always valid (a single
    /// digit) regardless of allowDecimalPoint, so it's never at risk of being rejected by this same
    /// filter it triggers.
    ///
    /// Also sets InputScope="Number" on this same inner InputBox as a complementary touch-keyboard/IME
    /// hint (shows a numeric glyph layout, e.g. on a touch/tablet keyboard) - as a mere hint, this
    /// applies uniformly to all six fields including SecondsBox: InputScopeNameValue has no "Decimal"
    /// member (confirmed empirically - referencing it is a compile error, CS0117), and this does NOT
    /// substitute for the BeforeTextChanging filtering above regardless, since InputScope only affects
    /// soft input methods and has no effect on what a hardware keyboard can type. NumberBox itself has
    /// no public, XAML-settable InputScope member in this WindowsAppSDK version - confirmed empirically:
    /// declaring InputScope="Number" directly on the NumberBox in XAML fails the XAML compiler with
    /// WMC0011 "Unknown member 'InputScope' on element 'NumberBox'", even though NumberBox's own default
    /// ControlTemplate template-binds this exact InputBox part's InputScope to it (see generic.xaml) -
    /// so it's set here in code, directly on the real TextBox part, instead.
    /// </summary>
    private static void HookIntervalCharacterFilter(TextBox inputBox, bool allowDecimalPoint)
    {
        inputBox.InputScope = new InputScope
        {
            Names = { new InputScopeName(InputScopeNameValue.Number) }
        };

        inputBox.BeforeTextChanging += (_, args) =>
        {
            var text = args.NewText;
            if (text.Length == 0)
            {
                return;
            }

            // SecondsBox alone (allowDecimalPoint) also accepts ',' as a decimal separator,
            // unconditionally - not locale-gated - normalized down to a canonical '.' so nothing
            // downstream (NumberBox's own commit-time formatter, ReadCommittedOrTypedValue's/
            // ReadLiveAdvancedFieldValue's double.TryParse reads) ever has to reason about ','
            // itself. TextBoxBeforeTextChangingEventArgs.NewText has no public setter - confirmed
            // empirically (CS0200: "Property or indexer ... cannot be assigned to -- it is read
            // only") - so this can't just rewrite args.NewText in place like a mutable buffer.
            // Instead: validate against a normalized copy, and if that copy is valid, Cancel this
            // edit outright (so the literal ',' this keystroke would have produced never applies)
            // and queue the already-normalized Text back onto the dispatcher instead - which runs
            // once this event returns and the (cancelled, so still previous) Text has settled.
            // Restores the caret to where it would have landed had a plain '.' been typed/pasted
            // at this same location instead of ',' - computed from the old selection plus how many
            // characters this edit is inserting (NewText's length above what survives after the old
            // selection is removed), not just assumed to be a single character, so this still lands
            // correctly for a multi-character paste containing a comma, not just a single keystroke.
            var normalized = allowDecimalPoint && text.Contains(',') ? text.Replace(',', '.') : text;

            var sawDecimalPoint = false;
            foreach (var ch in normalized)
            {
                if (ch is >= '0' and <= '9')
                {
                    continue;
                }

                if (allowDecimalPoint && ch == '.' && !sawDecimalPoint)
                {
                    sawDecimalPoint = true;
                    continue;
                }

                args.Cancel = true;
                return;
            }

            if (!ReferenceEquals(normalized, text))
            {
                args.Cancel = true;

                var oldText = inputBox.Text;
                var oldSelectionStart = inputBox.SelectionStart;
                var oldSelectionLength = inputBox.SelectionLength;
                var insertedLength = text.Length - (oldText.Length - oldSelectionLength);
                var newCaretPosition = Math.Clamp(oldSelectionStart + insertedLength, 0, normalized.Length);

                inputBox.DispatcherQueue.TryEnqueue(() =>
                {
                    inputBox.Text = normalized;
                    inputBox.SelectionStart = newCaretPosition;
                    inputBox.SelectionLength = 0;
                });
            }
        };
    }

    /// <summary>
    /// Keeps a NumberBox from ever sitting blank: whenever its inner InputBox's live text becomes empty
    /// (Backspace/Delete/Ctrl+A+Delete, or the built-in clear-text "X" button - see StyleClearButton,
    /// which just clears Text and does nothing else), immediately sets the box's Value to 0. Setting
    /// Value synchronously re-renders the inner TextBox's Text to "0" (NumberBox's own commit pipeline),
    /// so this also selects that new text (SelectAll) - so if the user is actually mid-replace (e.g.
    /// select-all then type a new number) the very next keystroke overwrites the "0" instead of
    /// appending after it (which would otherwise silently turn "0" + "4" into "04"). Used for
    /// MinutesBox/SecondsBox, which have no other TextChanged hook; the four Advanced fields get the
    /// equivalent check inline in AdvancedIntervalInputBox_TextChanged instead, since they already have
    /// a live TextChanged handler for their own cross-field sync.
    /// </summary>
    private static void HookBlankCoercion(NumberBox box)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => HookBlankCoercion(box);
            return;
        }

        inputBox.TextChanged += (_, _) =>
        {
            if (inputBox.Text.Length == 0)
            {
                // Setting box.Value here does NOT re-render the InputBox's Text while it still has
                // focus (confirmed empirically: NumberBox only reconciles Text from Value at
                // LostFocus/Enter, precisely so it doesn't stomp on live typing) - so the field would
                // stay visually blank until the user tabbed/clicked away, even though .Value was
                // already 0 underneath. Setting Text directly is what actually shows "0" right away;
                // NumberBox's own commit-time parse then reads this same "0" back into Value normally
                // once the box eventually does lose focus, no different than the user having typed it.
                inputBox.Text = "0";
                inputBox.SelectAll();
            }
        };
    }

    private static void StyleClearButton(NumberBox box)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => StyleClearButton(box);
            return;
        }

        StyleClearButton(inputBox);
    }

    private void HookAdvancedIntervalBoxLiveSync(NumberBox box, int maxLength)
    {
        // This runs in the constructor while AdvancedIntervalRow still defaults to
        // Visibility="Collapsed" (unless the persisted config already had Advanced enabled) - and a
        // Collapsed subtree is skipped during layout, so box's ControlTemplate (and thus its inner
        // "InputBox" part) normally wouldn't exist yet. Loaded fires once a FrameworkElement is
        // connected to the tree regardless of Visibility, i.e. BEFORE that template gets applied here -
        // so a Loaded-based fallback would fire too early, find no InputBox, and silently never hook,
        // for the entire remainder of the session (Loaded doesn't fire again just because Visibility
        // later changes). ApplyTemplate() sidesteps this by forcing the ControlTemplate to materialize
        // synchronously right now, independent of layout/visibility.
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            // Defense in depth in case ApplyTemplate ever isn't sufficient on its own (e.g. a future
            // WinUI change reintroduces a layout dependency) - retries the whole hook, including another
            // ApplyTemplate() call, once the box is actually loaded.
            box.Loaded += (_, _) => HookAdvancedIntervalBoxLiveSync(box, maxLength);
            return;
        }

        inputBox.MaxLength = maxLength;
        inputBox.TextChanged += AdvancedIntervalInputBox_TextChanged;

        // None of the four Advanced fields support fractional values (each is truncated to a whole
        // number - see ReadLiveAdvancedFieldValue/AdvancedIntervalBox_ValueChanged), so allowDecimalPoint
        // is always false here, unlike SecondsBox - see HookIntervalCharacterFilter's own doc comment.
        HookIntervalCharacterFilter(inputBox, allowDecimalPoint: false);

        // Suppressed, not styled-and-shown, for these four fields specifically - see
        // SuppressClearButton's own doc comment for why (confirmed empirically non-clickable here,
        // unlike Basic's MinutesBox/SecondsBox where StyleClearButton's equivalent button works fine).
        SuppressClearButton(inputBox);

        // box's own FontSize is deliberately 12 (see AdvancedIntervalRow's XAML comment) so its Header
        // caption stays legible/consistently sized against its longest sibling ("Milliseconds") - but
        // FontSize is an inherited property, so the inner InputBox's actual number text would otherwise
        // shrink along with it too, even though there's plenty of room here for regular-sized digits.
        // Setting FontSize directly on this TextBox (a distinct visual-tree element from the NumberBox
        // itself) gives it its own local value, overriding what it would've inherited, without touching
        // the Header presenter at all. 14 matches BasicIntervalRow's MinutesBox/SecondsBox, which never
        // override FontSize and so render at WinUI's default ControlContentThemeFontSize.
        inputBox.FontSize = 14;
    }

    /// <summary>
    /// Takes over driving the visibility of the built-in "clear text" (X) button that WinUI's TextBox
    /// ControlTemplate shows once its "InputBox" part has focus and non-empty text, instead of trusting
    /// the template's own show/hide logic - see below for why. Left at its default size/appearance
    /// (regular rectangular footprint) - only used for Basic's MinutesBox/SecondsBox (via
    /// StyleClearButton(NumberBox) below); the four Advanced fields use SuppressClearButton instead,
    /// see that method's doc comment for why.
    ///
    /// Visibility: confirmed empirically that the template's own "ButtonVisible"/"ButtonCollapsed"
    /// VisualStateManager states (which are supposed to show this button on focus + non-empty text)
    /// don't fire reliably for every NumberBox configuration in this app - so this manages Visibility
    /// explicitly via GotFocus/LosingFocus/TextChanged instead of depending on the template's own states.
    /// Uses LosingFocus rather than LostFocus specifically to dodge a click-through race - see that
    /// hookup's own comment below.
    ///
    /// Clicking the button clears Text same as always; HookBlankCoercion is what turns that resulting
    /// blank state into "0" instead of leaving the field empty.
    /// </summary>
    private static void StyleClearButton(TextBox inputBox)
    {
        inputBox.ApplyTemplate();

        if (FindDeleteButton(inputBox) is not { } deleteButton)
        {
            inputBox.Loaded += (_, _) => StyleClearButton(inputBox);
            return;
        }

        void UpdateVisibility() =>
            deleteButton.Visibility = inputBox.FocusState != FocusState.Unfocused && inputBox.Text.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        inputBox.GotFocus += (_, _) => UpdateVisibility();
        inputBox.TextChanged += (_, _) => UpdateVisibility();

        // Deliberately LosingFocus, not LostFocus: clicking DeleteButton itself moves focus away from
        // inputBox toward that same DeleteButton first (a Button takes pointer focus on press, before
        // its own Click fires on release) - confirmed empirically that reacting to plain LostFocus by
        // collapsing immediately raced with that click, hiding the button (and cancelling its own
        // pointer-capture/Click) before Click ever got to fire, so clicking it silently did nothing.
        // LosingFocus fires before focus actually moves and exposes where it's headed
        // (NewFocusedElement), so this can tell "focus is leaving to DeleteButton, about to be clicked -
        // don't hide out from under the click" apart from "focus is leaving somewhere else entirely -
        // really hide it".
        inputBox.LosingFocus += (_, e) =>
        {
            if (!ReferenceEquals(e.NewFocusedElement, deleteButton))
            {
                deleteButton.Visibility = Visibility.Collapsed;
            }
        };

        UpdateVisibility();
    }

    /// <summary>
    /// Permanently hides the built-in "clear text" (X) button for the four Advanced interval fields
    /// (Hours/Minutes/Seconds/Milliseconds) specifically - NOT because it can't be made visible there
    /// (StyleClearButton's Visibility-management approach above works fine at making it appear, sized
    /// and positioned correctly per UI Automation), but because clicking it there turned out to be
    /// inert: confirmed empirically, repeatedly, and by multiple independent methods (precise
    /// coordinate-targeted real mouse clicks via SendInput at the button's own UI-Automation-reported
    /// center, a grid of nearby coordinates covering and surrounding those bounds, and direct
    /// UIA InvokePattern.Invoke() on the button element itself, bypassing screen coordinates entirely)
    /// that clicking/invoking this button on any of the four Advanced fields never clears its text or
    /// changes its value - even immediately, even after forcing a blur afterward to rule out a
    /// display-refresh delay. The identical button on Basic's MinutesBox/SecondsBox (same control
    /// template, same code path, confirmed via the same InvokePattern method) works correctly and
    /// instantly every time. The one XAML difference between the two rows is
    /// SpinButtonPlacementMode="Hidden" (Advanced) vs "Compact" (Basic) - suspected but not confirmed to
    /// be what's suppressing hit-testing/event-routing to this button when spin buttons are hidden.
    /// A button that's visible and looks clickable but silently does nothing is worse than no button at
    /// all, so these four fields keep the old permanently-collapsed behavior instead: Visibility is
    /// forced to Collapsed once, and - because the template's own "ButtonVisible" VisualState
    /// re-animates it back to Visible on every relevant focus/text change regardless - a
    /// RegisterPropertyChangedCallback snaps it straight back to Collapsed every time that happens,
    /// rather than trying to fight the VisualStateManager with a one-time Setter. The never-blank rule
    /// (HookBlankCoercion / AdvancedIntervalInputBox_TextChanged's own blank-to-"0" check) still applies
    /// to these four fields regardless of this button's absence - Backspace/Delete/Ctrl+A+Delete alone
    /// already coerce them to "0" live, which was the more important half of this feature.
    /// </summary>
    private static void SuppressClearButton(TextBox inputBox)
    {
        inputBox.ApplyTemplate();

        if (FindDeleteButton(inputBox) is not { } deleteButton)
        {
            inputBox.Loaded += (_, _) => SuppressClearButton(inputBox);
            return;
        }

        deleteButton.Visibility = Visibility.Collapsed;
        deleteButton.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (sender, _) =>
        {
            if (sender is Button { Visibility: Visibility.Visible } button)
            {
                button.Visibility = Visibility.Collapsed;
            }
        });
    }

    private static Button? FindDeleteButton(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "DeleteButton" } deleteButton)
            {
                return deleteButton;
            }

            if (FindDeleteButton(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Advanced -> Basic conversion, run continuously on every keystroke in any of the four
    /// Hours/Minutes/Seconds/Milliseconds fields' own inner InputBox TextBox parts (see
    /// InitializeAdvancedIntervalLiveSync) while Advanced display is showing - not just at the moment
    /// the user switches back to Basic, and not just at commit. This is what keeps
    /// MinutesBox.Value/SecondsBox.Value correct at all times, which matters because
    /// PowerToggleButton_Checked's Start-time interval computation reads those two boxes (via
    /// ReadCommittedOrTypedValue) unconditionally and is otherwise untouched by this feature - if the
    /// advanced fields only converted back on toggle-off or on their own NumberBox.ValueChanged commit,
    /// starting automation via hotkey while Advanced is still showing (which never blurs any field)
    /// would silently use a stale interval from before the user's most recent, uncommitted edit here.
    /// Writing into MinutesBox.Value/SecondsBox.Value fires their existing ValueChanged handler
    /// (IntervalBox_ValueChanged) automatically, which is what actually persists the new interval to
    /// ConfigService - this method deliberately doesn't duplicate that.
    ///
    /// Also coerces this field's own blank text straight to "0" the instant it goes empty (Backspace/
    /// Delete/Ctrl+A+Delete - the built-in clear-text "X" button is suppressed on these four fields, see
    /// SuppressClearButton) - same never-blank rule as HookBlankCoercion applies to
    /// MinutesBox/SecondsBox, folded in here instead of a separate hook since these four fields already
    /// have this live TextChanged handler wired for their own cross-field sync. This sets the InputBox's
    /// Text directly (not sender.Value) - confirmed empirically that setting a NumberBox's Value while
    /// its InputBox still has focus does NOT re-render that Text (NumberBox only reconciles Text from
    /// Value at LostFocus/Enter, precisely so it doesn't stomp on live typing), so the field would stay
    /// visually blank until the user tabbed/clicked away otherwise, even with .Value already sitting at
    /// 0 underneath. Setting Text recurses back into this same handler once, harmlessly ("0" isn't
    /// blank, so the nested call just re-runs the sync below with the same numbers) - SelectAll follows
    /// it so that if the user is actually mid a select-all-then-retype edit, the very next keystroke
    /// replaces that "0" instead of appending after it. ReadLiveAdvancedFieldValue already treated blank
    /// as 0 for the cross-field sync below even before this change - this only fixes this field's own
    /// displayed text, which previously stayed blank until an eventual commit (see
    /// AdvancedIntervalBox_ValueChanged's matching NaN-to-0 coercion for that path).
    ///
    /// Each field is read independently via ReadLiveAdvancedFieldValue, which truncates to a whole
    /// number and clamps to that field's own Minimum/Maximum - deliberately NOT written back into that
    /// same field's own Value/Text from here (that visual snap-back is AdvancedIntervalBox_ValueChanged's
    /// job, at commit, so it doesn't disrupt whatever the user is still actively typing mid-edit).
    /// SecondsBox.Value takes the fractional remainder after whole minutes are removed (integer
    /// division for minutes, so any leftover milliseconds land in the fractional seconds part instead)
    /// - this is exactly why SecondsBox.Maximum had to move from 59.95 to 59.999 (see BasicIntervalRow's
    /// XAML comment), or up to 49ms of precision typed here would be silently clamped away on write-back.
    /// </summary>
    private void AdvancedIntervalInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingAdvancedIntervalFields || !_isAdvancedIntervalDisplayEnabled)
        {
            return;
        }

        if (sender is TextBox { Text.Length: 0 } inputBox)
        {
            inputBox.Text = "0";
            inputBox.SelectAll();
        }

        var hours = ReadLiveAdvancedFieldValue(HoursBox);
        var minutes = ReadLiveAdvancedFieldValue(AdvancedMinutesBox);
        var seconds = ReadLiveAdvancedFieldValue(AdvancedSecondsBox);
        var milliseconds = ReadLiveAdvancedFieldValue(MillisecondsBox);
        var totalMs = hours * 3_600_000L + minutes * 60_000L + seconds * 1000L + milliseconds;

        _isSyncingAdvancedIntervalFields = true;
        MinutesBox.Value = totalMs / 60_000;
        SecondsBox.Value = totalMs % 60_000 / 1000.0;
        _isSyncingAdvancedIntervalFields = false;
    }

    /// <summary>
    /// Reads one Advanced interval NumberBox's own live, uncommitted inner-InputBox text (not its
    /// .Value, which lags until commit - same reasoning as ReadCommittedOrTypedValue above), truncates
    /// it to a whole number, and clamps it to that box's own Minimum/Maximum. Empty/non-numeric/negative
    /// text all fall back to/clamp to 0 rather than throwing, since the field can legitimately sit
    /// empty or mid-edit while the user is typing.
    ///
    /// InvariantCulture, not CurrentCulture - see ReadCommittedOrTypedValue's own doc comment for the
    /// empirically-confirmed reasoning (CurrentCulture silently mis-parses a '.'-containing string by
    /// ~1000x on a comma-decimal Windows locale). Doesn't actually change behavior for these four
    /// fields specifically - they're digits-only (HookIntervalCharacterFilter never allows '.' or ','
    /// through for them), so there's never a separator character in play here either way - but keeping
    /// every parse of these six fields' text on the same culture avoids a latent inconsistency.
    /// </summary>
    private static long ReadLiveAdvancedFieldValue(NumberBox box)
    {
        var text = FindInputBoxText(box);
        if (string.IsNullOrWhiteSpace(text) ||
            !double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ||
            double.IsNaN(parsed))
        {
            return 0;
        }

        var truncated = TruncateToFractionDigits(parsed, fractionDigits: 0);
        return (long)Math.Clamp(truncated, Math.Max(0, box.Minimum), box.Maximum);
    }

    /// <summary>
    /// Commit-time (tier 1) clamp+truncation for the four Advanced interval NumberBoxes - fires when
    /// one of them actually commits (blur/Enter/programmatic Value set), unlike
    /// AdvancedIntervalInputBox_TextChanged above which fires live on every keystroke but never touches
    /// these boxes' own Value/Text. Clamps sender.Value to its own Minimum/Maximum and truncates
    /// (floors, never rounds) to a whole number, writing back only if that differs from the current
    /// value. Confirmed empirically (not assumed) that NumberBox does NOT auto-clamp typed/committed
    /// text to Minimum/Maximum on its own - typing e.g. "2000000" into HoursBox (Maximum 1666665) and
    /// committing left .Value at 2000000 unless this handler clamps it explicitly; Minimum/Maximum only
    /// constrain the (hidden, here) spin buttons/arrow keys, not committed typed text. Also confirmed
    /// setting sender.Value from inside this very handler does not cause NumberBox to raise a second
    /// ValueChanged for it (no reentrancy at all, so the differs-check here is just to skip a harmless
    /// no-op write, not to guard against recursion). Skips entirely while
    /// _isSyncingAdvancedIntervalFields is set (Basic -> Advanced population already writes
    /// pre-clamped, pre-truncated whole numbers - see PopulateAdvancedIntervalFieldsFromBasic).
    /// </summary>
    private void AdvancedIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isSyncingAdvancedIntervalFields)
        {
            return;
        }

        if (double.IsNaN(sender.Value))
        {
            // Belt-and-braces alongside AdvancedIntervalInputBox_TextChanged's live coercion: NaN means
            // this box committed (blur/Enter) while genuinely empty, which the never-blank rule (see
            // HookBlankCoercion's doc comment) says should land on 0, not stay blank.
            sender.Value = 0;
            return;
        }

        var clamped = Math.Clamp(sender.Value, sender.Minimum, sender.Maximum);
        var truncated = TruncateToFractionDigits(clamped, fractionDigits: 0);
        if (truncated != sender.Value)
        {
            sender.Value = truncated;
        }
    }

    /// <summary>
    /// Commit-time (tier 1) clamp+truncation for MinutesBox/SecondsBox, plus the existing config
    /// persistence. MinutesBox truncates (floors) to a whole number; SecondsBox truncates beyond 3
    /// decimal places - see TruncateToFractionDigits. Also clamps sender.Value to its own
    /// Minimum/Maximum first: confirmed empirically (not assumed) that NumberBox does NOT auto-clamp
    /// typed/committed text on its own - typing e.g. "99999960" into MinutesBox (Maximum 99999959) and
    /// committing left .Value at 99999960 unless this handler clamps it explicitly; Minimum/Maximum
    /// only constrain the spin buttons/arrow keys, not committed typed text.
    ///
    /// Also confirmed empirically: setting sender.Value from inside this very handler does NOT cause
    /// NumberBox to raise a second ValueChanged for it - the Value/Text update happens synchronously
    /// and silently, with no reentrancy at all. So there's no infinite-recursion risk to guard against
    /// here, but it also means persistence can't rely on "a recursive call with the already-corrected
    /// value handles it" - this method has exactly one pass per commit, so it must compute
    /// minutes/seconds AFTER applying the clamp+truncation write, in the same pass, or it would persist
    /// the pre-correction value.
    /// </summary>
    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        if (double.IsNaN(sender.Value))
        {
            // Belt-and-braces alongside HookBlankCoercion's live coercion: NaN means this box committed
            // (blur/Enter) while genuinely empty, which the never-blank rule says should land on 0, not
            // stay blank.
            sender.Value = 0;
        }
        else
        {
            var fractionDigits = ReferenceEquals(sender, SecondsBox) ? 3 : 0;
            var clamped = Math.Clamp(sender.Value, sender.Minimum, sender.Maximum);
            var truncated = TruncateToFractionDigits(clamped, fractionDigits);
            if (truncated != sender.Value)
            {
                sender.Value = truncated;
            }
        }

        var minutes = double.IsNaN(MinutesBox.Value) ? 0 : MinutesBox.Value;
        var seconds = double.IsNaN(SecondsBox.Value) ? 0 : SecondsBox.Value;

        ResetStatusToOffIfNotRunning();
        ConfigService.Update(c =>
        {
            c.IntervalMinutes = minutes;
            c.IntervalSeconds = seconds;
        });
    }

    private void AutoStopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetStopControlsEnabled(AutoStopToggle.IsOn);
        ResetStatusToOffIfNotRunning();
    }

    /// <summary>
    /// Opens the centered, modal Auto Stop configuration dialog. Seeds the staged working copies (and
    /// the RadioButton/NumberBox/DatePicker/TimePicker that edit them) from whatever was last
    /// configured - _lastConfiguredAutoStopMode/_autoStopCount/_stopDateTime, falling back to Count
    /// mode with the default count, and "now" for the date+time half, the very first time this is
    /// ever opened. Note this seeds from _lastConfiguredAutoStopMode, NOT _autoStopMode - so the
    /// dialog still offers up last session's choice even before the user has re-confirmed it this
    /// session (see _autoStopMode's declaration). Only an explicit OK (ContentDialogResult.Primary)
    /// commits the staged values into _autoStopMode/_lastConfiguredAutoStopMode/_autoStopCount/
    /// _stopDateTime, persists them, and updates the button's label; Cancel/Escape/any other dismissal
    /// leaves all of them completely untouched.
    /// </summary>
    private async void AutoStopButton_Click(object sender, RoutedEventArgs e)
    {
        // Falls back to "now" if the previously configured date+time has already passed, instead of
        // seeding the pickers with a stale, no-longer-reachable value - but only for seeding; this
        // doesn't touch _stopDateTime itself, which stays whatever it was until/unless OK is pressed.
        var baseline = _stopDateTime.HasValue && _stopDateTime.Value > DateTime.Now ? _stopDateTime.Value : DateTime.Now;
        _stagedStopDate = baseline.Date;
        _stagedStopTime = new TimeSpan(baseline.Hour, baseline.Minute, 0);
        _stagedAutoStopCount = _autoStopCount;

        StopDatePicker.Date = new DateTimeOffset(baseline.Date);
        StopTimePicker.Time = _stagedStopTime;
        AutoStopCountBox.Value = _stagedAutoStopCount;
        UpdateAutoStopCountBoxHeader();

        if (_lastConfiguredAutoStopMode == AutoStopMode.DateTime)
        {
            AutoStopByDateTimeRadioButton.IsChecked = true;
        }
        else
        {
            AutoStopByCountRadioButton.IsChecked = true;
        }

        AutoStopDialog.XamlRoot = Content.XamlRoot;

        // NOT calling RefreshStaleNumberBoxTextLayout() here, before ShowAsync: the dialog isn't
        // actually composed/shown yet at this point, so AutoStopCountBox's InputBox hasn't gone through
        // its own first real Arrange pass - see AutoStopDialog.Opened's subscription (constructor) and
        // RefreshStaleNumberBoxTextLayout's own doc comment for why the fix has to run after that pass.
        var result = await AutoStopDialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            _autoStopMode = AutoStopByCountRadioButton.IsChecked == true ? AutoStopMode.Count : AutoStopMode.DateTime;
            _lastConfiguredAutoStopMode = _autoStopMode;
            _autoStopCount = _stagedAutoStopCount;
            _stopDateTime = _stagedStopDate.Add(_stagedStopTime);

            UpdateAutoStopButtonLabel();
            ResetStatusToOffIfNotRunning();

            ConfigService.Update(c =>
            {
                c.AutoStopMode = _autoStopMode.ToString();
                c.AutoStopCount = _autoStopCount;
            });
        }
    }

    private void AutoStopByCountRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        AutoStopCountBox.IsEnabled = true;
        StopTimePicker.IsEnabled = false;
        StopDatePicker.IsEnabled = false;
    }

    private void AutoStopByDateTimeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        AutoStopCountBox.IsEnabled = false;
        StopTimePicker.IsEnabled = true;
        StopDatePicker.IsEnabled = true;
    }

    /// <summary>
    /// Also clamps sender.Value to [Minimum, Maximum] explicitly on commit - confirmed empirically
    /// elsewhere in this file (see IntervalBox_ValueChanged's own doc comment) that NumberBox does NOT
    /// actually enforce this for typed/committed text on its own, only for spin-button/arrow-key
    /// increments. The character filter (see HookIntervalCharacterFilter) already keeps typed text from
    /// ever exceeding Maximum (99999999 is exactly the largest 8-digit number, and MaxLength caps typed
    /// text at 8 digits), but Minimum=1 has no equivalent typed-side guard - typing a bare "0" is a
    /// valid single digit that would otherwise stick as committed as-is.
    /// </summary>
    private void AutoStopCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        var clamped = Math.Clamp(args.NewValue, sender.Minimum, sender.Maximum);
        if (clamped != sender.Value)
        {
            sender.Value = clamped;
        }

        _stagedAutoStopCount = (int)clamped;
    }

    private void StopDatePicker_DateChanged(object sender, DatePickerValueChangedEventArgs args)
    {
        _stagedStopDate = args.NewDate.Date;
    }

    private void StopTimePicker_TimeChanged(object sender, TimePickerValueChangedEventArgs args)
    {
        _stagedStopTime = args.NewTime;
    }

    /// <summary>
    /// Sets AutoStopCountBox's Header to "clicks" or "jiggles" to match whichever Auto Click/Jiggle Mode
    /// is currently selected on the main window - called only when AutoStopDialog is about to open,
    /// since ModeSegmentedControl is unreachable (the dialog is modal) while it's already showing.
    /// </summary>
    private void UpdateAutoStopCountBoxHeader()
    {
        AutoStopCountBox.Header = _isJiggleModeSelected ? "jiggles" : "clicks";
    }

    /// <summary>
    /// Refreshes AutoStopButtonLabel.Text and AutoStopIcon.Symbol to reflect whichever Auto Stop mode
    /// is currently committed (_autoStopMode) - the "Configure" placeholder if never configured,
    /// FormatAutoStopDateTime's relative-day summary for DateTime mode (Calendar icon), or "After N
    /// click(s)/jiggle(s)" - reusing FormatActionCount, the same helper the running power-button counter
    /// uses, so the wording matches exactly - for Count mode (Refresh icon).
    /// </summary>
    private void UpdateAutoStopButtonLabel()
    {
        switch (_autoStopMode)
        {
            case AutoStopMode.Count:
                var mode = _isJiggleModeSelected ? AutomationMode.Jiggle : AutomationMode.Click;
                AutoStopButtonLabel.Text = $"After {FormatActionCount(_autoStopCount, mode)}";
                AutoStopIcon.Glyph = "\uEF3B"; //Replay
                break;
            case AutoStopMode.DateTime when _stopDateTime.HasValue:
                AutoStopButtonLabel.Text = FormatAutoStopDateTime(_stopDateTime.Value);
                AutoStopIcon.Glyph = "\uEC92"; //DateTime
                break;
            default:
                AutoStopButtonLabel.Text = "Date & time or counter";
                AutoStopIcon.Glyph = "\uE93A"; //MiniExpand
                break;
        }
    }

    /// <summary>
    /// "Yesterday \u00B7 HH:mm"/"Today \u00B7 HH:mm"/"Tomorrow \u00B7 HH:mm" when value's date is
    /// within a day of today, falling back to the previous "yyyy-MM-dd \u00B7 HH:mm" absolute format
    /// otherwise - kept correct across a day boundary by ScheduleNextMidnightRefresh re-calling
    /// UpdateAutoStopButtonLabel at midnight, since "today"/"tomorrow" is only ever true relative to
    /// whenever this happens to be evaluated.
    /// </summary>
    private static string FormatAutoStopDateTime(DateTime value)
    {
        var dayLabel = (value.Date - DateTime.Today).Days switch
        {
            -1 => "Yesterday",
            0 => "Today",
            1 => "Tomorrow",
            _ => value.ToString("yyyy-MM-dd")
        };
        return $"{dayLabel} \u00B7 {value:HH:mm}";
    }

    /// <summary>
    /// (Re)schedules _autoStopLabelMidnightTimer to fire once at the next local midnight - see that
    /// field's own comment for why. Recomputes the exact interval fresh every time (rather than
    /// assuming a fixed 24h), so it stays correct across DST transitions. Runs unconditionally
    /// regardless of _autoStopMode - UpdateAutoStopButtonLabel's own switch already no-ops correctly
    /// for Count/None, so gating this on DateTime mode would only add complexity for no real benefit.
    /// </summary>
    private void ScheduleNextMidnightRefresh()
    {
        var now = DateTime.Now;
        _autoStopLabelMidnightTimer.Interval = now.Date.AddDays(1) - now;
        _autoStopLabelMidnightTimer.Start();
    }
}
