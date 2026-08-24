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
using MouseUtil.Controls;
using MouseUtil.Interop;
using MouseUtil.Services;
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
    private const double WindowHeightDip = 576;

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
    private bool _isSpinModeSelected;

    // Randomize-interval toggle (RandomizeIntervalButton, in the Interval card's header row) -
    // deliberately never persisted to ConfigService, unlike most other toggles in this app: it
    // always starts Off on launch, regardless of what it was set to last session. See
    // RandomizeIntervalButton_Click/UpdateRandomizeIntervalIndicator and
    // MouseAutomationEngine.Start's randomizeInterval parameter for where this actually takes effect.
    private bool _isRandomizeIntervalEnabled;

    // Advanced-interval-display mode (Hours/Minutes/Seconds/Milliseconds fields, in place of the plain
    // Minutes/Seconds ones) - unlike _isRandomizeIntervalEnabled above, this one mirrors a genuinely
    // persisted setting (config.ShowAdvancedIntervalDisplay, owned by SettingsPanel's "Display
    // advanced interval" toggle - see UpdateAdvancedIntervalDisplayMode) rather than always starting
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
    // rather than the raw _isSpinModeSelected bool, e.g. seeding TrayIconService's tooltip while
    // inactive (see UpdateState's mode parameter).
    private AutomationMode CurrentSelectedMode => _isSpinModeSelected ? AutomationMode.Spin : AutomationMode.Click;

    // Click/spin action counter (see UpdatePowerButtonRunningDisplay). _completedActionCount counts
    // every action Engine_ActionPerformed reports, with no exclusion for the first one - the first
    // action after the startup countdown (or the first action fired immediately via the hotkey, see
    // skipStartupCountdown) counts as action #1, same as every action after it. The button switches
    // from "Stop" to showing the counter as soon as _completedActionCount > 0, rather than a separate
    // flag. _runningMode is captured once at Start() time (rather than re-read from
    // _isSpinModeSelected) since ModeSwitchButton is disabled mid-run anyway, but reading a captured
    // value is more explicit/robust. _isPointerOverPowerButton tracks hover state so the counter text
    // yields to "Stop" while the pointer is over the button.
    private int _completedActionCount;
    private AutomationMode _runningMode;
    private bool _isPointerOverPowerButton;

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
    // TrayIconService_StartRequested) - only ever set for Click mode, since Spin mode already skips
    // the startup grace unconditionally regardless of this flag (see the needsStartupGrace check in
    // MouseAutomationEngine.RunLoopAsync), so "Start Spin Mode" from the tray needs no equivalent.
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

    // Shared with SettingsPanel (see InitializeSettingsPanel's UpdateChecker wiring) so both the
    // launch-time auto-check below and Settings' own manual "Check for updates" button reuse the same
    // HttpClient instead of each hitting GitHub Releases separately - even though the two deliberately
    // don't share UI state (see UpdateAvailableButton's own comment in MainWindow.xaml).
    // _autoDetectedUpdate holds InitializeAutoUpdateCheck's result for as long as UpdateAvailableButton
    // stays visible - read by UpdateAvailableFlyoutButton_Click when the user actually clicks through.
    private readonly UpdateService _updateService = new();
    private UpdateCheckResult? _autoDetectedUpdate;

    public MainWindow()
    {
        InitializeComponent();

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

        Title = "MouseUtil";
        SystemBackdrop = new MicaBackdrop();

        ConfigureWindowSizingAndMaximizeBehavior();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarGrid);

        SizeWindow();
        CenterOnScreen();
        InitializeSettingsPanel();

        LoadConfigIntoUi();
        InitializeAdvancedIntervalLiveSync();

        // AutoStopCountBox (the "After a number of clicks/spins" field in AutoStopDialog) gets the
        // same digits-only character filter and MaxLength cap as the interval fields above - reuses
        // SetInputBoxMaxLength/HookIntervalCharacterFilter as-is, not interval-specific despite living
        // alongside interval setup. No decimal point allowed here at all (this field is always a whole
        // count), and 8 matches its own Maximum's digit count (99999999).
        SetInputBoxMaxLength(AutoStopCountBox, maxLength: 8, allowDecimalPoint: false);
        SettingsVersionText.Text = $"v{UpdateService.GetCurrentVersionString()}";
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
        InitializeAutoUpdateCheck();

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

        // AccentBrushSource/MutedBrushSource (hidden TextBlocks bound via {ThemeResource}) update
        // their own Foreground automatically when the theme changes, but UpdateModeIndicators()
        // only reads a one-time snapshot of those brushes and assigns it directly to
        // ClickModeIcon/SpinModeIcon/ClickModeBox/SpinModeBox - so without this, those controls keep
        // showing colors from whichever theme was active the last time the mode switch was clicked,
        // until the user clicks it again. Re-running UpdateModeIndicators() on every actual theme
        // change (covers both explicit Theme dropdown selection and the OS theme changing while
        // "System" is selected) keeps them in sync immediately instead.
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateModeIndicators();
            UpdateRandomizeIntervalIndicator();
        };

        // Sets RandomizeIntervalIcon's initial AutomationProperties.Name/Foreground declaratively
        // from _isRandomizeIntervalEnabled's actual (always-false-on-launch) value, rather than
        // relying on the XAML defaults happening to already match it.
        UpdateRandomizeIntervalIndicator();
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
        _settingsPanel.UpdateChecker = _updateService;

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
        _settingsPanel.ShowActionCounterChanged += (_, _) => UpdatePowerButtonRunningDisplay();
        _settingsPanel.ShowAdvancedIntervalDisplayChanged += (_, _) => UpdateAdvancedIntervalDisplayMode();
        _settingsPanel.CloseAppRequested += (_, _) =>
        {
            _isClosingConfirmed = true;
            Close();
        };

        SettingsHost.Children.Add(_settingsPanel);
    }

    /// <summary>
    /// Silently checks GitHub Releases once at startup, gated on Settings' "Automatically check for
    /// updates" toggle (default off - see AppConfig.AutoCheckForUpdates) - unlike SettingsPanel's own
    /// manual button, this never surfaces a "Checking..."/error state anywhere, since the user never
    /// explicitly asked for it at this moment; a failure here just means UpdateAvailableButton stays
    /// hidden, identical to "no update available" from the user's point of view. Deliberately does
    /// NOT touch SettingsPanel.UpdateButton's own idle state - the two are independent (see
    /// UpdateAvailableButton's comment in MainWindow.xaml for why).
    /// </summary>
    private async void InitializeAutoUpdateCheck()
    {
        if (!ConfigService.Load().AutoCheckForUpdates)
        {
            return;
        }

        try
        {
            var result = await _updateService.CheckForUpdateAsync(UpdateService.GetCurrentVersionString(), CancellationToken.None);
            if (result.IsUpdateAvailable)
            {
                _autoDetectedUpdate = result;
                UpdateAvailableFlyoutButtonLabel.Text = $"Update to v{result.LatestVersion}";
                UpdateAvailableButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or FormatException)
        {
            // Silent by design - see this method's doc comment.
        }
    }

    /// <summary>
    /// Mirrors the download-then-close half of SettingsPanel.UpdateButton_Click, but standalone since
    /// this button lives outside SettingsPanel - see UpdateAvailableButton's comment in
    /// MainWindow.xaml. No "first click checks, second click downloads" distinction here: by the time
    /// this button is visible at all, _autoDetectedUpdate is already populated, so every click goes
    /// straight to downloading (or opening the release page, if the release has no .exe asset yet).
    /// </summary>
    private async void UpdateAvailableFlyoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoDetectedUpdate is not { } update)
        {
            return;
        }

        if (update.DownloadUrl is null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
            return;
        }

        UpdateAvailableFlyoutButton.IsEnabled = false;
        UpdateAvailableFlyoutButtonLabel.Text = "Downloading...";
        try
        {
            var installerPath = await _updateService.DownloadInstallerAsync(update.DownloadUrl, CancellationToken.None);
            _updateService.LaunchInstaller(installerPath);
            _isClosingConfirmed = true;
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            UpdateAvailableFlyoutButtonLabel.Text = "Couldn't download the update. Try again later.";
            UpdateAvailableFlyoutButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Fires whenever the Flyout closes for any reason (light-dismiss, Escape, or the successful
    /// download/close path above) - hiding UpdateAvailableButton unconditionally is correct either
    /// way: on a genuine dismiss-without-updating it's the intended one-shot-notification behavior
    /// (see UpdateAvailableButton's comment in MainWindow.xaml), and on the successful path the app is
    /// already closing anyway, so touching this button's visibility is moot.
    /// </summary>
    private void UpdateAvailableFlyout_Closed(object sender, object e)
    {
        UpdateAvailableButton.Visibility = Visibility.Collapsed;
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

        ModeSubtitleTextBlock.Text = "Settings";

        // MainContentGrid's Visibility.Collapsed no longer happens here directly - it's deferred to
        // AnimatePanelTransition's completion, once it has actually slid off screen (see that
        // method's own comment for why a Collapsed element can't be animated in the first place).
        AnimatePanelTransition(outgoing: MainContentGrid, incoming: SettingsOverlay, reverse: false,
            onCompleted: () => SettingsBackButton.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// Reverses ShowSettingsOverlay: slides the overlay back out (see ShowSettingsOverlay's own
    /// comment for why SettingsButton doesn't need any explicit handling here either).
    /// ModeSubtitleTextBlock is set back to the mode name immediately/synchronously here, mirroring
    /// what ShowSettingsOverlay already does for the opposite direction, so the subtitle updates
    /// instantly rather than waiting on the 300ms slide-back animation. UpdateModeIndicators()'s call
    /// in onCompleted below re-sets the exact same text once the animation finishes (harmless
    /// redundancy - by then SettingsOverlay.Visibility is Collapsed so its own guard lets it through)
    /// but is still needed there because it also drives icon colors and mode-box borders, which are
    /// separate from this fix and should keep waiting for the transition to finish.
    /// </summary>
    private void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsTransitioning)
        {
            return;
        }

        _settingsPanel.HandleHostClosing();
        ModeSubtitleTextBlock.Text = _isSpinModeSelected ? "Spin mode" : "Auto click";

        AnimatePanelTransition(outgoing: SettingsOverlay, incoming: MainContentGrid, reverse: true,
            onCompleted: UpdateModeIndicators);
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
    /// Handles "Start Auto Click"/"Start Spin Mode" from the tray context menu (only reachable while
    /// inactive - see TrayIconService.ShowContextMenu). Forces the mode selector to the requested mode
    /// (mirroring what ModeSwitchButton_Click does, via the shared SetSelectedMode) and then starts
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
    /// hotkey press does. Spin mode is deliberately left alone: MouseAutomationEngine.RunLoopAsync's
    /// needsStartupGrace check already skips the startup grace for Spin unconditionally, so there's no
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
    /// Handles "Pause spinning on movement" from the tray context menu (only reachable while inactive -
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

        _isSpinModeSelected = config.LastMode == "Spin";
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

    private void SetInputsEnabled(bool enabled)
    {
        ModeSwitchButton.IsEnabled = enabled;
        MinutesBox.IsEnabled = enabled;
        SecondsBox.IsEnabled = enabled;
        AutoStopCheckBox.IsEnabled = enabled;
        SetStopControlsEnabled(enabled && AutoStopCheckBox.IsChecked == true);

        // IntervalCaptionTextBlock is a DimmableLabel control (see Controls/DimmableLabel.cs) -
        // setting IsEnabled here drives its Normal/Disabled VisualState transition declaratively, the
        // same way MinutesBox/SecondsBox's own Header text dims when their IsEnabled flips false.
        IntervalCaptionTextBlock.IsEnabled = enabled;

        // Locked while running so the randomize-interval behavior can't change out from under an
        // in-progress run. The two disabled cases still look deliberately different for the button's
        // own chrome (see RandomizeIntervalButton.Resources in MainWindow.xaml): Unchecked+Disabled
        // stays fully invisible (ToggleButtonBackgroundDisabled/BorderBrushDisabled are overridden to
        // Transparent - there's nothing to indicate when the setting is off), while Checked+Disabled
        // draws an outline (ToggleButtonBackgroundCheckedDisabled is Transparent,
        // ToggleButtonBorderBrushCheckedDisabled is a gray) so the user can still see at a glance that
        // the setting is on even while it's locked. RandomizeIntervalIcon's own Foreground/Opacity
        // aren't set here at all, though - they're set from within UpdateRandomizeIntervalIndicator
        // instead (called below, after IsEnabled is updated so it can see the new locked state), which
        // now gives the icon the exact same dimmed look while locked regardless of Checked state - see
        // that method's doc comment for why.
        RandomizeIntervalButton.IsEnabled = enabled;
        UpdateRandomizeIntervalIndicator();

        // The four Hours/Minutes/Seconds/Milliseconds fields get the same locked-while-running
        // treatment as MinutesBox/SecondsBox above - the values within them can't change out from
        // under an in-progress run. (SettingsPanel's own "Display advanced interval" toggle is
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

        if (AutoStopCheckBox.IsChecked == true)
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
        if (AutoStopCheckBox.IsChecked == true)
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
        SetStatusText("Off", StatusTone.Muted);

        SetInputsEnabled(false);
        PowerToggleIcon.Glyph = "\uEE95"; // Stop glyph - pressing the pill now would stop the engine.
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.Text = "Stop";
        AutomationProperties.SetName(PowerToggleButton, "Power, Stop");

        _engine.Start(mode, interval, stopAt, stopAfterActionCount, _settingsPanel.PauseOnMovement, _isRandomizeIntervalEnabled, skipStartupCountdown);
        _trayIconService.UpdateState(isRunning: true, isPaused: false, mode: mode);
        UpdateModeIndicators();
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
        // "Stopped after N spin" text below with a stale buffered "Spinning in X" tick.
        _statusHoldActive = false;
        _pendingStatusAfterHold = null;

        UpdateModeIndicators();

        // Re-renders from _autoStopMode's current value - a no-op if this run left it untouched
        // (Auto Stop was enabled/configured), or reflects the "Configure" placeholder if this run's
        // Checked handler just reset it to None because Auto Stop was disabled.
        UpdateAutoStopButtonLabel();

        SetInputsEnabled(true);
        PowerToggleIcon.Glyph = "\uE768"; // Play glyph - pressing the pill now would start the engine.
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.Text = "Start";
        AutomationProperties.SetName(PowerToggleButton, "Power, Start");

        if (showShrug)
        {
            SetStatusText(SelfInflictedOffStatusText, StatusTone.Muted);
        }
        else
        {
            // Shows the counter result instead of plain "Off" only when the setting is on AND at
            // least one counted action actually happened; otherwise it falls back to "Off" exactly
            // as before.
            var text = _settingsPanel.ShowActionCounter && _completedActionCount > 0
                ? $"Stopped after {FormatActionCount(_completedActionCount, _runningMode)}"
                : "Off";
            SetStatusText(text, StatusTone.Muted);
        }
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

    /// <summary>
    /// Refreshes PowerToggleLabel's text to either "Stop" or the running click/spin counter, and
    /// PowerToggleIcon's visibility to match, while the engine is running. This is the single place
    /// that decides Stop-vs-counter, so the icon's Visibility is set in the same conditional rather
    /// than duplicated elsewhere: Visible while showing "Stop", Collapsed while showing the counter
    /// (Collapsed, not Hidden, so the counter text can be centered with no leftover gap where the icon
    /// was). Never touches the button's other visual style (colors).
    ///
    /// Shows "Stop" (icon visible) whenever the counter setting is off, the pointer is hovering the
    /// button, or no action has fired yet (i.e. still in the startup countdown -
    /// _completedActionCount == 0); shows the singular/plural "{count} click(s)" or "{count} spin(s)"
    /// text (icon collapsed) for whichever mode is actually running otherwise - starting at "1
    /// click"/"1 spin" the instant the first action fires, since every action is counted (no free
    /// startup action). No-ops while the button isn't checked/running - hovering while stopped
    /// shouldn't do anything.
    /// </summary>
    private void UpdatePowerButtonRunningDisplay()
    {
        if (PowerToggleButton.IsChecked != true)
        {
            return;
        }

        var showCounter = _settingsPanel.ShowActionCounter && !_isPointerOverPowerButton && _completedActionCount > 0;

        PowerToggleLabel.Text = showCounter ? FormatActionCount(_completedActionCount, _runningMode) : "Stop";
        PowerToggleIcon.Visibility = showCounter ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Formats a completed-action count as "{count} click"/"{count} clicks" (Click mode) or
    /// "{count} spin"/"{count} spins" (Spin mode), singular only when count == 1.
    /// </summary>
    private static string FormatActionCount(int count, AutomationMode mode)
    {
        var noun = mode == AutomationMode.Click ? "click" : "spin";
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

    // How long Spin mode's one-shot "Starting now" (StatusKind.SpinStarting) stays on screen before
    // later status reports are allowed to overwrite it - purely cosmetic (so it doesn't flash by
    // faster than a human can read it), independent of MouseAutomationEngine's actual interval
    // timer, which fires the next spin on its own schedule regardless of this hold.
    private static readonly TimeSpan SpinStartingStatusHoldDuration = TimeSpan.FromMilliseconds(500);
    private bool _statusHoldActive;
    private StatusChangedEventArgs? _pendingStatusAfterHold;

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

            if (e.Kind == StatusKind.SpinStarting)
            {
                _statusHoldActive = true;
                _pendingStatusAfterHold = null;
                _ = ReleaseStatusHoldAfterDelayAsync();
            }
        });
    }

    private async Task ReleaseStatusHoldAfterDelayAsync()
    {
        await Task.Delay(SpinStartingStatusHoldDuration);

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
        // Guards against a race in the countdown-heavy paths (Click mode's startup grace, Spin
        // mode's pause-on-movement resume countdown): the engine's background loop reports the
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

        var tone = e.Kind switch
        {
            StatusKind.Starting => StatusTone.Success,
            StatusKind.SpinStarting => StatusTone.Success,
            StatusKind.Imminent => StatusTone.Critical,
            StatusKind.Paused => StatusTone.Caution,
            _ => StatusTone.Muted
        };
        SetStatusText(e.Text, tone);
        UpdateTaskbarProgress(e);
        _trayIconService.UpdateState(isRunning: true, isPaused: e.Kind == StatusKind.Paused, mode: _runningMode);
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
    /// Single click-handling surface for the Auto click / Spin mode switch: flips the selected mode
    /// as one cohesive unit (rather than two independent mutually-exclusive ToggleButtons), refreshes
    /// all visual indicators, and persists the choice so the app reopens in the same mode next launch.
    /// </summary>
    private void ModeSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMode(_isSpinModeSelected ? AutomationMode.Click : AutomationMode.Spin);
    }

    /// <summary>
    /// Sets the selected Auto Click/Spin mode to a specific value, as opposed to
    /// ModeSwitchButton_Click's "flip to whichever mode isn't currently selected" - shared by that
    /// click handler above and by the tray context menu's "Start Auto Click"/"Start Spin Mode" items
    /// (see TrayIconService_StartRequested), which need to force one specific mode rather than toggle
    /// it. No-ops if the requested mode is already selected, so a tray-driven start in the mode that's
    /// already selected doesn't replay the switch animation/effects for nothing.
    /// </summary>
    private void SetSelectedMode(AutomationMode mode)
    {
        var isSpin = mode == AutomationMode.Spin;
        if (_isSpinModeSelected == isSpin)
        {
            return;
        }

        _isSpinModeSelected = isSpin;
        UpdateModeIndicators();
        (_isSpinModeSelected ? SpinModePopStoryboard : ClickModePopStoryboard).Begin();
        (_isSpinModeSelected ? SpinModeIconSpinStoryboard : ClickModeIconWiggleStoryboard).Begin();
        ResetStatusToOffIfNotRunning();

        // "After N clicks/spins" (see UpdateAutoStopButtonLabel) must track whichever mode is now
        // selected, even if the user never reopens AutoStopDialog after switching modes.
        UpdateAutoStopButtonLabel();

        ConfigService.Update(c => c.LastMode = _isSpinModeSelected ? "Spin" : "Click");

        // Keeps the tray tooltip's mode name live even while inactive, since the mode selection can
        // change with no Start/Stop in between (see TrayIconService.UpdateState's other call sites).
        _trayIconService.UpdateState(isRunning: _engine.IsRunning, isPaused: false, mode: mode);
    }

    private static readonly Thickness NoModeBoxBorder = new(0);
    private static readonly Thickness SelectedModeBoxBorder = new(1.5);
    private const double SelectedModeBoxBackgroundOpacity = 0.20;
    private const double DisabledModeBoxBorderOpacity = 0.5;
    private const double UnselectedModeIconRunningOpacity = 0.5;

    /// <summary>
    /// Updates everything that reflects the current click/spin mode selection: the accent-colored
    /// border outline around whichever box is selected (see ApplySelectedModeBoxStyle for the
    /// selected box's border/background, which also depends on whether the engine is running). The
    /// unselected box gets BorderThickness=0 so it renders with no border at all (not just a
    /// transparent-brush border occupying space). Also updates the two mode-icon colors (accent vs.
    /// muted) and their opacity - the unselected icon dims to UnselectedModeIconRunningOpacity while
    /// the engine is running (full opacity otherwise), matching the selected box's border dimming to
    /// reinforce that the whole selector is temporarily inactive; the selected icon's opacity is
    /// never touched - plus the title bar subtitle text and the switch's accessible name.
    /// </summary>
    private void UpdateModeIndicators()
    {
        var accent = AccentBrushSource.Foreground;

        ClickModeIcon.Foreground = _isSpinModeSelected ? MutedBrushSource.Foreground : accent;
        SpinModeIcon.Foreground = _isSpinModeSelected ? accent : MutedBrushSource.Foreground;

        ClickModeIcon.Opacity = _isSpinModeSelected && _engine.IsRunning ? UnselectedModeIconRunningOpacity : 1;
        SpinModeIcon.Opacity = !_isSpinModeSelected && _engine.IsRunning ? UnselectedModeIconRunningOpacity : 1;

        ClickModeBox.BorderThickness = _isSpinModeSelected ? NoModeBoxBorder : SelectedModeBoxBorder;
        SpinModeBox.BorderThickness = _isSpinModeSelected ? SelectedModeBoxBorder : NoModeBoxBorder;

        ApplySelectedModeBoxStyle(ClickModeBox, isSelected: !_isSpinModeSelected, accent);
        ApplySelectedModeBoxStyle(SpinModeBox, isSelected: _isSpinModeSelected, accent);

        // Leaves the subtitle alone while Settings is the active/transitioning-to screen (see
        // ShowSettingsOverlay, which sets it to "Settings") - otherwise a mode change triggered while
        // Settings is open (e.g. the tray menu's "Start Auto Click"/"Start Spin Mode", via
        // SetSelectedMode) would stomp it back to the mode name despite Settings still being what's
        // actually on screen. SettingsOverlay stays Visible for the whole time Settings is open or
        // mid-transition either way (see AnimatePanelTransition), only going Collapsed once fully back
        // on the main screen, so this check covers both states correctly.
        if (SettingsOverlay.Visibility != Visibility.Visible)
        {
            ModeSubtitleTextBlock.Text = _isSpinModeSelected ? "Spin mode" : "Auto click";
        }

        AutomationProperties.SetName(
            ModeSwitchButton,
            _isSpinModeSelected ? "Mode switch, Spin mode selected" : "Mode switch, Click mode selected");
    }

    /// <summary>
    /// Sets a mode box's border and background for its selected/unselected state, additionally
    /// dimming down to a disabled look while the engine is running:
    ///
    /// - Unselected: no border, no background (unchanged either way).
    /// - Selected + idle: accent-colored border (unchanged from before) plus a same-colored
    ///   background at SelectedModeBoxBackgroundOpacity, so the selection reads as a filled chip
    ///   instead of just an outline.
    /// - Selected + running: border switches to DisabledBrushSource's grayscale color at
    ///   DisabledModeBoxBorderOpacity, and the background is removed entirely. The mode selector is
    ///   already IsEnabled=false while running (see SetInputsEnabled) - this is purely the visual
    ///   reinforcement of that disabled state. The icon's color (set by the caller) is untouched.
    /// </summary>
    private void ApplySelectedModeBoxStyle(Border box, bool isSelected, Brush accent)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);

        if (!isSelected)
        {
            box.BorderBrush = transparent;
            box.Background = transparent;
            return;
        }

        if (_engine.IsRunning)
        {
            var disabledColor = ((SolidColorBrush)DisabledBrushSource.Foreground).Color;
            box.BorderBrush = new SolidColorBrush(disabledColor) { Opacity = DisabledModeBoxBorderOpacity };
            box.Background = transparent;
        }
        else
        {
            box.BorderBrush = accent;
            var accentColor = ((SolidColorBrush)accent).Color;
            box.Background = new SolidColorBrush(accentColor) { Opacity = SelectedModeBoxBackgroundOpacity };
        }
    }

    /// <summary>
    /// Syncs _isRandomizeIntervalEnabled from RandomizeIntervalButton.IsChecked (the real source of
    /// truth now that this is a ToggleButton) and refreshes the button's own indicator.
    /// RandomizeIntervalButton is locked (IsEnabled=false) via SetInputsEnabled while automation is
    /// running, same as MinutesBox/SecondsBox/ModeSwitchButton - so this can only ever fire while
    /// idle, and the setting can't change out from under an in-progress run.
    /// </summary>
    private void RandomizeIntervalButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _isRandomizeIntervalEnabled = RandomizeIntervalButton.IsChecked == true;
        UpdateRandomizeIntervalIndicator();
    }

    /// <summary>
    /// Sets RandomizeIntervalIcon's Foreground and Opacity to match _isRandomizeIntervalEnabled and
    /// RandomizeIntervalButton.IsEnabled - always explicit local values, never left to inherit from
    /// the ContentPresenter (whose Foreground the default ToggleButton ControlTemplate re-targets to
    /// ToggleButtonForegroundChecked while Checked) and never ClearValue'd.
    ///
    /// Three states:
    ///   - Idle (Unchecked, unlocked): Foreground = PrimaryBrushSource (TextFillColorPrimaryBrush,
    ///     matching IntervalCaptionTextBlock's own undimmed base color - white in Dark theme, black in
    ///     Light theme), Opacity = 1. Needs to stay clearly brighter than the "INTERVAL" caption
    ///     beside it (IntervalCaptionTextBlock, which sits at a constant Opacity = 0.6 - see
    ///     IntervalCaptionLabelStyle) so the icon still visibly reads as an interactive button/toggle,
    ///     not a plain label.
    ///   - Checked AND unlocked (sitting on the accent-filled pill): Foreground = OnAccentBrushSource
    ///     (TextOnAccentFillColorPrimaryBrush - the same brush the template itself uses for
    ///     ToggleButtonForegroundChecked: black in Dark theme, white in Light theme), Opacity = 1.
    ///     This is the only state where the icon actually sits on a filled accent background, so it's
    ///     the only state that needs the accent-contrast color.
    ///   - Locked (automation running), regardless of Checked state: Foreground = DisabledBrushSource
    ///     (TextFillColorDisabledBrush), Opacity = 0.6 - the exact same Foreground/Opacity combination
    ///     IntervalCaptionLabelStyle's own Disabled VisualState uses for the "INTERVAL" caption beside
    ///     it. Deliberately identical regardless of Checked state - this used to differ (Locked+Checked
    ///     stayed at PrimaryBrushSource/0.5, brighter than Locked+Unchecked's DisabledBrushSource/0.6),
    ///     on the theory that the brighter look helped signal "still on while locked". That backfired
    ///     once a second button that briefly existed here (AdvancedIntervalDisplayButton, styled
    ///     identically, since removed - see SettingsPanel's "Display advanced interval" toggle
    ///     instead) could sit right next to this one in a different Checked state while both were
    ///     locked at once: whichever one happened to be Checked rendered visibly brighter than the
    ///     other for no reason a user could infer, since both are simply "locked, nothing you can do
    ///     about it right now" - confirmed via side-by-side pixel-contrast comparison in both themes at
    ///     the time, and the fix stayed even after that button did not. RandomizeIntervalButton's own outline
    ///     (ToggleButtonBorderBrushCheckedDisabled, present only in the Locked+Checked case) is what
    ///     communicates on/off while locked here, not the icon - keeping OnAccentBrushSource would also
    ///     be wrong regardless, since the pill background goes Transparent once locked (see
    ///     ToggleButtonBackgroundCheckedDisabled in RandomizeIntervalButton.Resources), and that brush's
    ///     accent-contrast color would render close to invisible against the app's own background
    ///     instead.
    ///
    /// Also updates the button's AutomationProperties.Name/ToolTip so screen readers and tooltips
    /// announce the current state, not just "Randomize interval" with no indication of on/off.
    /// </summary>
    private void UpdateRandomizeIntervalIndicator()
    {
        bool isLocked = !RandomizeIntervalButton.IsEnabled;
        bool isCheckedAndUnlocked = !isLocked && _isRandomizeIntervalEnabled;

        RandomizeIntervalIcon.Foreground = isCheckedAndUnlocked
            ? OnAccentBrushSource.Foreground
            : isLocked
                ? DisabledBrushSource.Foreground
                : PrimaryBrushSource.Foreground;
        RandomizeIntervalIcon.Opacity = isCheckedAndUnlocked ? 1 : isLocked ? 0.6 : 0.8;

        AutomationProperties.SetName(
            RandomizeIntervalButton,
            _isRandomizeIntervalEnabled ? "Randomize interval, On" : "Randomize interval, Off");
        ToolTipService.SetToolTip(
            RandomizeIntervalButton,
            _isRandomizeIntervalEnabled ? "Randomize interval: On" : "Randomize interval: Off");
    }

    /// <summary>
    /// Applies _settingsPanel.ShowAdvancedIntervalDisplay - SettingsPanel's own persisted "Display
    /// advanced interval" toggle, the sole source of truth now that AdvancedIntervalDisplayButton
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
    /// happens in SettingsPanel.ShowAdvancedIntervalDisplayToggle_Toggled, so this method doesn't
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
    /// for all six interval fields plus AutoStopCountBox (the "clicks/spins" count field in
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

    private void AutoStopCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        SetStopControlsEnabled(AutoStopCheckBox.IsChecked == true);
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
    /// Sets AutoStopCountBox's Header to "clicks" or "spins" to match whichever Auto Click/Spin Mode
    /// is currently selected on the main window - called only when AutoStopDialog is about to open,
    /// since ModeSwitchButton is unreachable (the dialog is modal) while it's already showing.
    /// </summary>
    private void UpdateAutoStopCountBoxHeader()
    {
        AutoStopCountBox.Header = _isSpinModeSelected ? "spins" : "clicks";
    }

    /// <summary>
    /// Refreshes AutoStopButtonLabel.Text and AutoStopIcon.Symbol to reflect whichever Auto Stop mode
    /// is currently committed (_autoStopMode) - the "Configure" placeholder if never configured,
    /// FormatAutoStopDateTime's relative-day summary for DateTime mode (Calendar icon), or "After N
    /// click(s)/spin(s)" - reusing FormatActionCount, the same helper the running power-button counter
    /// uses, so the wording matches exactly - for Count mode (Refresh icon).
    /// </summary>
    private void UpdateAutoStopButtonLabel()
    {
        switch (_autoStopMode)
        {
            case AutoStopMode.Count:
                var mode = _isSpinModeSelected ? AutomationMode.Spin : AutomationMode.Click;
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
    /// "Yesterday  HH:mm"/"Today  HH:mm"/"Tomorrow  HH:mm" when value's date is within a day of today,
    /// falling back to the previous "yyyy-MM-dd  HH:mm" absolute format otherwise - kept correct across
    /// a day boundary by ScheduleNextMidnightRefresh re-calling UpdateAutoStopButtonLabel at midnight,
    /// since "today"/"tomorrow" is only ever true relative to whenever this happens to be evaluated.
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
        return $"{dayLabel}  {value:HH:mm}";
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
