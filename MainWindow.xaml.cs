using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MouseUtil.Controls;
using MouseUtil.Interop;
using MouseUtil.Services;
using System.Globalization;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
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

    // Global Start/Stop hotkey (F6 by default, user-configurable in Settings) - see
    // GlobalHotkeyService, HotkeyService_HotkeyPressed, and the HotkeyButton_Click/_KeyDown
    // recording flow. _hotkeyModifiers/_hotkeyKey mirror the currently-registered combination
    // (loaded from AppConfig at startup, updated whenever recording a new one succeeds) so a failed
    // re-registration attempt can roll back to them. _startTriggeredByHotkey is set just before
    // HotkeyService_HotkeyPressed programmatically checks PowerToggleButton, and consumed (read then
    // cleared) at the top of PowerToggleButton_Checked - this is what tells that handler to skip the
    // normal startup countdown and fire the first action immediately, since a real user click on the
    // button itself should still use the countdown as before.
    private readonly GlobalHotkeyService _hotkeyService = new();
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _startTriggeredByHotkey;
    private bool _isRecordingHotkey;

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

    // "Close to system tray" setting (Settings flyout CloseToTrayToggle, persisted via
    // AppConfig.CloseToTray) - mirrors the toggle's current IsOn state so AppWindow_Closing can read it
    // synchronously without touching the UI thread's control tree from inside that handler. See
    // TrayIconService for the actual Shell_NotifyIcon plumbing, and CloseToTrayToggle_Toggled /
    // TrayIconService_ExitRequested below for how it's kept in sync and how "Exit" from the tray menu
    // reuses CloseConfirmationDialog.
    private readonly TrayIconService _trayIconService = new();
    private bool _closeToTray;

    // "Show timer in taskbar" setting (Settings flyout ShowTaskbarProgressToggle, persisted via
    // AppConfig.ShowTaskbarProgress) - mirrors the ITaskbarList3-driven progress bar overlaid on this
    // app's taskbar icon while automation runs (see Services/TaskbarProgressService). _lastTaskbarProgress
    // holds the most recent numeric progress reported by the engine (see StatusChangedEventArgs.Progress)
    // so a "Paused" report with no countdown of its own (movement just detected) can still repaint the
    // bar amber/yellow at wherever it already was, instead of resetting it to 0.
    private readonly TaskbarProgressService _taskbarProgressService = new();
    private bool _showTaskbarProgress;
    private double _lastTaskbarProgress;

    public MainWindow()
    {
        InitializeComponent();

        Title = "MouseUtil";
        SystemBackdrop = new MicaBackdrop();

        ConfigureWindowSizingAndMaximizeBehavior();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarGrid);

        SizeWindow();
        CenterOnScreen();
        LoadConfigIntoUi();
        SettingsVersionText.Text = $"v{GetAppVersionString()}";
        UpdateTitleBarCaptionSpacer();
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

        // WinUI auto-focuses the first focusable control in tab order (MinutesBox, being first in
        // the Interval card) once the window activates and its control template is ready - there's no
        // dedicated "initial focus target" API to redirect that default assignment up front, and it
        // fires later than Activated/Loaded (a direct Focus() call from either of those still loses
        // the race), so the only deterministic hook is to react to the assignment itself landing on
        // MinutesBox and redirect it. This only ever fires once (unsubscribes immediately), so it
        // only intercepts that one startup assignment - clicking or Tabbing into MinutesBox normally
        // afterward is completely unaffected, and Tab order itself is untouched.
        MinutesBox.GotFocus += MinutesBox_GotFocus;

        // AccentBrushSource/MutedBrushSource (hidden TextBlocks bound via {ThemeResource}) update
        // their own Foreground automatically when the theme changes, but UpdateModeIndicators()
        // only reads a one-time snapshot of those brushes and assigns it directly to
        // ClickModeIcon/SpinModeIcon/ClickModeBox/SpinModeBox - so without this, those controls keep
        // showing colors from whichever theme was active the last time the mode switch was clicked,
        // until the user clicks it again. Re-running UpdateModeIndicators() on every actual theme
        // change (covers both explicit Theme dropdown selection and the OS theme changing while
        // "System" is selected) keeps them in sync immediately instead.
        RootGrid.ActualThemeChanged += (_, _) => UpdateModeIndicators();
    }

    /// <summary>
    /// Fires exactly once - see the comment on the GotFocus subscription above. Redirects that one
    /// startup focus assignment to SecondsBox instead of leaving it on MinutesBox.
    /// </summary>
    private void MinutesBox_GotFocus(object sender, RoutedEventArgs e)
    {
        MinutesBox.GotFocus -= MinutesBox_GotFocus;
        SecondsBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Subclasses this window's WndProc (see GlobalHotkeyService) and registers the hotkey loaded
    /// from config (_hotkeyModifiers/_hotkeyKey, set by LoadConfigIntoUi). If registration fails -
    /// e.g. another app already owns that exact combination - the hotkey simply doesn't fire until
    /// the user picks a different one in Settings; there's no other app state to roll back to at
    /// startup, unlike a failed re-registration during recording (see HotkeyButton_KeyDown).
    /// </summary>
    private void InitializeGlobalHotkey()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        _hotkeyService.AttachToWindow(hwnd);
        _hotkeyService.TryRegister(_hotkeyModifiers, _hotkeyKey);
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;

        // Single-instance enforcement (see App.OnLaunched/Services/SingleInstanceService): a second
        // launch attempt posts this message to bring THIS window to the foreground instead of ever
        // opening a duplicate. Reuses the WndProc subclass GlobalHotkeyService already installed for
        // WM_HOTKEY above, rather than adding a second one.
        _hotkeyService.RegisterMessageHandler(SingleInstanceService.ShowWindowMessageId, ActivateAndBringToForeground);
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
        _trayIconService.ShowRequested += (_, _) => ActivateAndBringToForeground();
        _trayIconService.ExitRequested += TrayIconService_ExitRequested;

        UpdateTrayIconVisibility();
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

    // Reads the running exe's file version (set via MouseUtil.csproj's <Version>, kept in sync with
    // installer\MouseUtil.iss's MyAppVersion) for display in the Settings flyout header. This app is
    // unpackaged (WindowsPackageType=None, no Package.appxmanifest), so there's no Package.Current to
    // query - FileVersionInfo on the entry assembly's own path is the unpackaged equivalent. Falls back
    // to AssemblyName's Version (always present) if the file version is ever unavailable.
    private static string GetAppVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        return !string.IsNullOrWhiteSpace(fileVersion) ? fileVersion : assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private void LoadConfigIntoUi()
    {
        _isInitializing = true;

        var config = ConfigService.Load();

        MinutesBox.Value = config.IntervalMinutes;
        SecondsBox.Value = config.IntervalSeconds;
        PauseOnMovementToggle.IsOn = config.PauseOnMovement;
        ShowActionCounterToggle.IsOn = config.ShowActionCounter;

        _closeToTray = config.CloseToTray;
        CloseToTrayToggle.IsOn = _closeToTray;

        _showTaskbarProgress = config.ShowTaskbarProgress;
        ShowTaskbarProgressToggle.IsOn = _showTaskbarProgress;

        _hotkeyModifiers = config.HotkeyModifiers;
        _hotkeyKey = config.HotkeyKey;
        HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);

        ThemeComboBox.SelectedItem = config.Theme switch
        {
            "Light" => ThemeLightItem,
            "Dark" => ThemeDarkItem,
            _ => ThemeSystemItem
        };
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

        // Settings that would otherwise let the user change how the running automation behaves out
        // from under it: the hotkey (recording a new one mid-run makes no sense - it's the same
        // input that's currently controlling this very run), and two display/behavior toggles it
        // wouldn't make sense to flip while running. Only IsEnabled changes here - IsOn/values are
        // left completely untouched, so whatever was configured stays configured; this only blocks
        // interaction, matching MinutesBox/SecondsBox/AutoStopCheckBox above. Theme is deliberately
        // not included here - switching themes while automation runs is still allowed.
        HotkeyButton.IsEnabled = enabled;
        ShowActionCounterToggle.IsEnabled = enabled;
        PauseOnMovementToggle.IsEnabled = enabled;

        // IntervalCaptionTextBlock/HotkeyCaptionTextBlock/ShowActionCounterCaption/
        // PauseOnMovementCaption are DimmableLabel controls (see Controls/DimmableLabel.cs) - setting
        // IsEnabled here drives their Normal/Disabled VisualState transition declaratively, the same
        // way MinutesBox/SecondsBox's own Header text dims when their IsEnabled flips false. No
        // Foreground assignment needed on this side at all. ShowActionCounterCaption/
        // PauseOnMovementCaption mirror the toggles they label immediately above - CloseToTrayToggle's
        // row label is a plain TextBlock (not DimmableLabel) and intentionally isn't touched here,
        // since that toggle is never disabled while running.
        IntervalCaptionTextBlock.IsEnabled = enabled;
        HotkeyCaptionTextBlock.IsEnabled = enabled;
        ShowActionCounterCaption.IsEnabled = enabled;
        PauseOnMovementCaption.IsEnabled = enabled;
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
        // hotkey press can never leak into a later, genuinely button-click-triggered start.
        var skipStartupCountdown = _startTriggeredByHotkey;
        _startTriggeredByHotkey = false;

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

        var mode = _isSpinModeSelected ? AutomationMode.Spin : AutomationMode.Click;

        var minutes = ReadCommittedOrTypedValue(MinutesBox);
        var seconds = ReadCommittedOrTypedValue(SecondsBox);
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

        _engine.Start(mode, interval, stopAt, stopAfterActionCount, PauseOnMovementToggle.IsOn, skipStartupCountdown);
        UpdateModeIndicators();
    }

    // NumberBox only re-parses typed input into Value on focus loss/Enter, so a hotkey-triggered start
    // (which never moves focus) would otherwise read the last-committed Value and ignore text the user
    // just typed but hasn't blurred away from yet. NumberBox's own Text DP turned out not to be a
    // reliable stand-in either - measured (via logging) up to 60+ms of lag behind what's on screen,
    // presumably from its own internal validation being debounced/async. Reading the template's actual
    // "InputBox" TextBox part directly is the one source with zero indirection - it's the literal
    // control the user is typing into.
    private static double ReadCommittedOrTypedValue(NumberBox box)
    {
        var liveText = FindInputBoxText(box) ?? box.Text;

        if (double.TryParse(liveText, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed) &&
            !double.IsNaN(parsed))
        {
            return Math.Clamp(parsed, box.Minimum, box.Maximum);
        }

        return double.IsNaN(box.Value) ? 0 : box.Value;
    }

    private static string? FindInputBoxText(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Name: "InputBox" } inputBox)
            {
                return inputBox.Text;
            }

            if (FindInputBoxText(child) is { } found)
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
            var text = ShowActionCounterToggle.IsOn && _completedActionCount > 0
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

        var showCounter = ShowActionCounterToggle.IsOn && !_isPointerOverPowerButton && _completedActionCount > 0;

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
        _isSpinModeSelected = !_isSpinModeSelected;
        UpdateModeIndicators();
        (_isSpinModeSelected ? SpinModePopStoryboard : ClickModePopStoryboard).Begin();
        (_isSpinModeSelected ? SpinModeIconSpinStoryboard : ClickModeIconWiggleStoryboard).Begin();
        ResetStatusToOffIfNotRunning();

        // "After N clicks/spins" (see UpdateAutoStopButtonLabel) must track whichever mode is now
        // selected, even if the user never reopens AutoStopDialog after switching modes.
        UpdateAutoStopButtonLabel();

        ConfigService.Update(c => c.LastMode = _isSpinModeSelected ? "Spin" : "Click");
    }

    private static readonly Thickness NoModeBoxBorder = new(0);
    private static readonly Thickness SelectedModeBoxBorder = new(2);
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

        ModeSubtitleTextBlock.Text = _isSpinModeSelected ? "Spin mode" : "Auto click";
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

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (ThemeComboBox.SelectedItem is ComboBoxItem { Tag: string theme })
        {
            ApplyTheme(theme);
            ConfigService.Update(c => c.Theme = theme);
        }
    }

    /// <summary>
    /// If the Settings flyout closes (light-dismiss, clicking elsewhere, etc.) while mid-recording,
    /// cancel the capture exactly like Escape would - otherwise _isRecordingHotkey stays stuck true,
    /// HotkeyButton_Click's re-entry guard permanently no-ops on it, and the label is left showing
    /// "Press a key combination…" forever instead of the actual current hotkey.
    /// </summary>
    private void SettingsFlyout_Closed(object? sender, object e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = false;
        HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
    }

    /// <summary>
    /// Enters hotkey-recording mode: the next key HotkeyButton_KeyDown sees (that isn't itself a bare
    /// modifier) becomes the new global hotkey. Ignored while already recording, so a second click
    /// mid-capture can't start a redundant/overlapping capture.
    /// </summary>
    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = true;
        HotkeyErrorTextBlock.Visibility = Visibility.Collapsed;
        HotkeyButtonLabel.Text = "Press a key combination…";
    }

    /// <summary>
    /// Captures the next key while recording: Escape cancels (reverts the label, keeps the previous
    /// hotkey registered/persisted, no save); a bare modifier key (Ctrl/Alt/Shift/Win alone) is
    /// ignored so recording keeps waiting for the actual key; any other key finalizes the combination
    /// together with whatever modifiers are currently held (queried via InputKeyboardSource, the
    /// WinUI3-desktop equivalent of UWP's CoreWindow.GetKeyState). Marks every key while recording as
    /// Handled so the button's own default key handling (e.g. Space/Enter invoking Click) doesn't
    /// interfere with capture.
    ///
    /// On success: registers immediately (GlobalHotkeyService.TryRegister unregisters the old one
    /// first), persists it, and updates the label - "save" is implicit in a successful capture, no
    /// separate confirm step, matching how modern Windows shortcut editors behave. On failure (the
    /// combination is already claimed by another app): rolls back to the previous hotkey so the app
    /// is never left with nothing registered, and shows an inline error instead of persisting.
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

        if (_hotkeyService.TryRegister(modifiers, virtualKey))
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
            _hotkeyService.TryRegister(previousModifiers, previousKey);
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

    private void PauseOnMovementToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ResetStatusToOffIfNotRunning();
        ConfigService.Update(c => c.PauseOnMovement = PauseOnMovementToggle.IsOn);
    }

    /// <summary>
    /// Updates the tray icon's visibility immediately (rather than only taking effect on the next
    /// close/reopen) and persists the setting. See AppWindow_Closing for the actual close-vs-hide
    /// behavior this drives.
    /// </summary>
    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _closeToTray = CloseToTrayToggle.IsOn;
        UpdateTrayIconVisibility();
        ConfigService.Update(c => c.CloseToTray = _closeToTray);
    }

    /// <summary>
    /// Turning this off immediately clears the taskbar progress bar (rather than leaving it frozen at
    /// whatever it last showed until the run stops) since UpdateTaskbarProgress no-ops entirely while
    /// _showTaskbarProgress is false and won't repaint it on its own.
    /// </summary>
    private void ShowTaskbarProgressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _showTaskbarProgress = ShowTaskbarProgressToggle.IsOn;
        if (!_showTaskbarProgress)
        {
            _taskbarProgressService.Clear();
        }

        ConfigService.Update(c => c.ShowTaskbarProgress = _showTaskbarProgress);
    }

    private void ShowActionCounterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        // Cosmetic/display setting, not automation configuration - does not reset a just-finished
        // run's "Stopped after N clicks/spins" status. If a run is currently in progress and
        // displaying (or would display) the counter, refresh the button text immediately rather than
        // waiting for the next action/hover event to pick up the new setting.
        UpdatePowerButtonRunningDisplay();
        ConfigService.Update(c => c.ShowActionCounter = ShowActionCounterToggle.IsOn);
    }

    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
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

    private void AutoStopCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        _stagedAutoStopCount = (int)args.NewValue;
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
    /// the same "yyyy-MM-dd  HH:mm" summary as before for DateTime mode (Calendar icon), or "After N
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
                AutoStopIcon.Glyph = "\uE72C"; //Refresh
                break;
            case AutoStopMode.DateTime when _stopDateTime.HasValue:
                AutoStopButtonLabel.Text = _stopDateTime.Value.ToString("yyyy-MM-dd  HH:mm");
                AutoStopIcon.Glyph = "\uE787"; //Calendar
                break;
            default:
                AutoStopButtonLabel.Text = "Date & time or counter";
                AutoStopIcon.Glyph = "\uE70F"; //Edit
                break;
        }
    }
}