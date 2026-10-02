using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Text;
using MouseUtil.Controls;
using MouseUtil.Interop;
using MouseUtil.Models;
using MouseUtil.Services;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MouseUtil;

/// <summary>
/// Which Auto Stop mode (if any) is currently configured - see AutoStopDialog/AutoStopButton_Click.
/// None means the feature has never been configured this session (AutoStopButton still shows its
/// "Configure" placeholder). Count, DateTime, Duration, and Time are mutually exclusive, selected via
/// AutoStopDialog's AutoStopSegmentedControl (Date &amp; time/Duration/Action count). DateTime (a
/// one-shot date+time) and Time (a recurring every-day time) share that first segment;
/// AutoStopPickDateToggle inside AutoStopDateTimeContainer disambiguates which one an OK commit
/// resolves to (see SegmentIndexToAutoStopMode).
/// </summary>
public enum AutoStopMode
{
    None,
    Count,
    DateTime,
    Duration,
    Time
}

public sealed partial class MainWindow : Window
{
    private const double WindowWidthDip = 400;
    private const double WindowHeightDip = 506;

    private const double MinimumIntervalSeconds = 0.02;

    // Shown instead of "Off"/"Stopped after N clicks" when this run's first automated click happened
    // to land on and toggle off the Start/Stop button itself (see PowerToggleButton_Unchecked /
    // MouseAutomationEngine.WasFirstClickJustInjected). Verbatim string so the backslash is literal.
    private const string SelfInflictedOffStatusText = @"¯\_(ツ)_/¯";

    // Shown by EnterIntervalPresetCardEditMode while editing/adding an interval preset, and restored by
    // IntervalBox_ValueChanged once a rejected 0 value (see AcceptIntervalPresetEdit) is fixed live.
    private const string EditingIntervalPresetStatusText = "Editing preset · start disabled";

    // Dimmed relative to every other status (SetStatusText's default Opacity=1) since this is secondary
    // context, not the actual automation status.
    private const double EditingIntervalPresetStatusOpacity = 0.8;

    private readonly MouseAutomationEngine _engine = new();
    private readonly UISettings _uiSettings = new();
    private bool _isInitializing;
    private string _themePreference = "System";
    private DateTime? _stopDateTime;

    // Committed values for AutoStopMode.Duration ("After a period of time") and AutoStopMode.Time (a
    // recurring every-day time). Unlike _stopDateTime (session-only), these mirror persisted config
    // (AppConfig.AutoStopDurationHours/Minutes/Seconds, AutoStopTimeHour/Minute - loaded in
    // LoadConfigIntoUi) and are resolved into an absolute stop DateTime only when a run actually starts
    // (DateTime.Now + duration, or the next occurrence of the time - see PowerToggleButton_Checked),
    // since MouseAutomationEngine only understands an absolute stopAt. _timeStopTime is shared by both
    // AutoStopPickDateToggle states (Date&amp;time vs. recurring Time): AutoStopButton_Click seeds
    // StopTimePicker from it (defaulting to 17:00), and OK always writes it back regardless of which
    // mode is committed.
    private TimeSpan _stopDuration;
    private TimeSpan _timeStopTime;

    // Absolute stop time for the currently running AutoStopMode.Duration run, computed once at Start
    // (DateTime.Now + _stopDuration - see PowerToggleButton_Checked) purely so ApplyEngineStatus can
    // show a static target time on AutoStopButtonLabel (see UpdateRunningDurationTargetTimeLabel).
    // Display-only; null when not running under this mode, reset every Start.
    private DateTime? _durationStopDeadline;

    // Absolute stop time for the currently running AutoStopMode.Time run, computed once at Start (the
    // next occurrence of _timeStopTime) so ApplyEngineStatus can show a "Today"/"Tomorrow" target time
    // (see UpdateRunningTimeTargetTimeLabel). Display-only; null when not running under this mode,
    // reset every Start.
    private DateTime? _timeStopDeadline;

    // Fires once at the next local midnight to refresh AutoStopButtonLabel's Yesterday/Today/Tomorrow
    // relative-day wording (see UpdateAutoStopButtonLabel/ScheduleNextMidnightRefresh) - nothing else
    // re-renders that label if the app sits idle across a day boundary with a DateTime auto-stop set.
    private readonly DispatcherTimer _autoStopLabelMidnightTimer = new();

    // Drives AutoStopButtonLabel's text and the stop condition PowerToggleButton_Checked passes to the
    // engine. Always starts at None on launch regardless of what was persisted, and only becomes
    // Count/DateTime/etc. once the user confirms AutoStopDialog with OK this session (see
    // AutoStopButton_Click) - avoids acting on a stale summary the user never re-reviewed. Reset back
    // to None whenever a run starts with Auto Stop unchecked.
    // _lastConfiguredAutoStopMode seeds AutoStopDialog's RadioButtons the first time it opens each
    // session (from config.AutoStopMode, then tracks _autoStopMode from the first OK onward) - kept
    // separate so the dialog still offers the last real choice instead of forcing a reconfigure.
    private AutoStopMode _autoStopMode = AutoStopMode.None;
    private AutoStopMode _lastConfiguredAutoStopMode = AutoStopMode.None;
    private int _autoStopCount = 100;
    private bool _isJiggleModeSelected;

    // Guards ModeSegmentedControl_SelectionChanged from replaying mode-switch side effects (icon
    // wiggle/spin, status/auto-stop refresh, persisted LastMode, tray icon update) when
    // UpdateModeIndicators sets ModeSegmentedControl.SelectedIndex purely to sync the control to
    // _isJiggleModeSelected, as opposed to a real user click or tray-driven change (SetSelectedMode)
    // which should still replay all of that. Only true for the duration of that one assignment.
    private bool _isSyncingModeSelection;

    // Randomize-interval toggle (RandomizeIntervalToggle, inside IntervalOptionsFlyout) - persisted to
    // ConfigService.RandomizeInterval (restored in LoadConfigIntoUi) and kept in sync by
    // RandomizeIntervalToggle_Toggled/UpdateRandomizeIntervalIndicator. MouseAutomationEngine.Start's
    // randomizeInterval parameter is where this actually takes effect.
    private bool _isRandomizeIntervalEnabled;

    // Committed quick-preset values for the Interval card's presets flyout - variable-length (up to
    // MaxIntervalPresets), index-aligned across all four arrays, rendered onto the flyout's apply
    // buttons by RefreshIntervalPresetFlyoutButtons. Loaded once via LoadIntervalPresets, then mutated
    // only by a successful AcceptIntervalPresetEdit (editing/adding happens inline on the Interval card,
    // not in a separate dialog). List<int> (not int[]) so adding a preset doesn't need a resize dance.
    private List<int> _intervalPresetHours = new();
    private List<int> _intervalPresetMinutes = new();
    private List<int> _intervalPresetSeconds = new();
    private List<int> _intervalPresetMilliseconds = new();

    // Upper bound on how many interval presets the flyout's 2-rows-of-3 button grid can show.
    private const int MaxIntervalPresets = 6;

    // Index-aligned lookup tables for the flyout's up-to-6 one-shot apply buttons/menu items - built
    // once in the constructor, after InitializeComponent so the named elements already exist.
    private Button[] _intervalPresetFlyoutButtons = null!;
    private MenuFlyoutItem[] _intervalPresetUseMenuItems = null!;

    // True while IntervalPresetsEditButton (a ToggleButton) is checked. Cleared when a preset or the Add
    // tile is picked (IntervalPresetFlyoutButton_Click), the flyout is light-dismissed
    // (IntervalOptionsFlyout_Closed), or the user unchecks the button. While true, clicking a preset
    // button selects it for editing instead of applying it (unless delete is also armed - see
    // _isIntervalPresetDeleteArmed), and the Add tile becomes visible in the next open slot under the cap.
    private bool _isIntervalPresetEditArmed;

    // True while IntervalPresetsDeleteButton (only visible while Edit is armed) is checked. While true,
    // clicking a preset button deletes that slot (DeleteIntervalPreset) instead of selecting it for
    // editing - this takes priority over the plain edit-armed branch. Stays true across multiple deletes
    // (not one-shot, unlike editing); reset to false whenever Edit is un-armed.
    private bool _isIntervalPresetDeleteArmed;

    // Tagged union of the two actions Undo can reverse: a single-preset delete (carries the deleted
    // preset's own values, re-appended on Undo) or a wholesale Restore-to-defaults (carries a snapshot of
    // all four lists from just before the reset, restored wholesale on Undo). `record` is used only for
    // the positional-property boilerplate - value equality is never relied on.
    private abstract record IntervalPresetUndoAction;
    private sealed record DeletePresetUndoAction((int Hours, int Minutes, int Seconds, int Milliseconds) Preset) : IntervalPresetUndoAction;
    private sealed record RestoreDefaultsUndoAction(List<(int Hours, int Minutes, int Seconds, int Milliseconds)> PreviousPresets) : IntervalPresetUndoAction;

    // Stack of interval-preset-mutating actions (delete / restore-to-defaults) for the current
    // flyout-open session, reversible in LIFO order via UndoLastIntervalPresetAction. Cleared only by
    // IntervalOptionsFlyout_Opening - NOT by arming/un-arming Edit or Delete - so an action taken via
    // right-click while unarmed stays undo-able for the rest of the session.
    // IntervalPresetsUndoButton.Visibility = _isIntervalPresetEditArmed OR (stack non-empty AND NOT
    // _isIntervalPresetUndoDismissed): Undo is always shown while Edit is armed, even with an empty
    // stack. While unarmed, dismissing it once must hide it without losing the underlying history -
    // hence the separate _isIntervalPresetUndoDismissed flag below, since a plain bool can't distinguish
    // "never armed" from "armed, then un-armed" (both are false).
    private readonly List<IntervalPresetUndoAction> _intervalPresetUndoStack = new();

    // True only in the window after Edit has been explicitly un-armed since it was last armed, the
    // flyout opened, or a mutating action happened. Makes IntervalPresetsUndoButton hide while unarmed
    // without touching _intervalPresetUndoStack (re-arming still reveals any existing history). Cleared
    // by arming Edit back on, any new delete/restore, and IntervalOptionsFlyout_Opening.
    private bool _isIntervalPresetUndoDismissed;

    // Non-null while an existing interval preset slot is selected for inline editing on the Interval
    // card; null otherwise, including while _isAddingIntervalPreset (mutually exclusive with this).
    // IntervalBox_ValueChanged checks this (and _isAddingIntervalPreset) to skip persisting
    // MinutesBox/SecondsBox's live edits to the real committed interval while staging a preset value.
    private int? _editingIntervalPresetIndex;

    // True while composing a brand-new interval preset (the flyout's Add tile was clicked while edit
    // mode was armed) - mutually exclusive with _editingIntervalPresetIndex above.
    private bool _isAddingIntervalPreset;

    // MinutesBox/SecondsBox's real, non-preset value at the moment preset edit mode started - restored
    // (FinishIntervalPresetEditMode) once editing ends, whether accepted or discarded. Same role as
    // AutoStop's _preEditDurationHours/Minutes/Seconds, but against these two boxes directly since they
    // stay the source of truth for the live interval regardless of Basic/Advanced display.
    private double _preEditIntervalMinutes;
    private double _preEditIntervalSeconds;

    // Advanced-interval-display mode (Hours/Minutes/Seconds/Milliseconds fields in place of the plain
    // Minutes/Seconds ones). Mirrors persisted config.ShowAdvancedIntervalDisplay (SettingsPanel's
    // "Interval display" dropdown - see UpdateAdvancedIntervalDisplayMode). Purely a display-mode
    // switch: MinutesBox.Value/SecondsBox.Value stay the actual source of truth, so this never feeds
    // into MouseAutomationEngine.
    private bool _isAdvancedIntervalDisplayEnabled;

    // Guards AdvancedIntervalBox_ValueChanged/AdvancedIntervalInputBox_TextChanged/
    // PopulateAdvancedIntervalFieldsFromBasic against reentrancy while one side of the Basic<->Advanced
    // conversion is programmatically writing into the other side's controls - without this, each
    // ValueChanged/TextChanged fired by that population would immediately try to convert back and
    // overwrite MinutesBox/SecondsBox mid-population with a transient, incomplete total.
    private bool _isSyncingAdvancedIntervalFields;

    // Guards IntervalDisplayToggle_Toggled against reentrancy while UpdateAdvancedIntervalDisplayMode
    // programmatically assigns IntervalDisplayToggle.IsOn to mirror SettingsPanel's "Interval display"
    // RadioButtons.
    private bool _isSyncingIntervalDisplayToggle;

    // The mode currently selected in the UI regardless of whether automation is running, as an
    // AutomationMode rather than the raw _isJiggleModeSelected bool (e.g. seeding TrayIconService's
    // tooltip while inactive).
    private AutomationMode CurrentSelectedMode => _isJiggleModeSelected ? AutomationMode.Jiggle : AutomationMode.Click;

    // Click/jiggle action counter (see UpdatePowerButtonRunningDisplay). _completedActionCount counts
    // every action Engine_ActionPerformed reports; the button switches from "Stop" to showing the
    // counter as soon as it's > 0. _runningMode is captured once at Start() time rather than re-read
    // from _isJiggleModeSelected, since ModeSegmentedControl is disabled mid-run anyway.
    // _isPointerOverPowerButton tracks hover so the counter text yields to "Stop" while hovered.
    private int _completedActionCount;
    private AutomationMode _runningMode;
    private bool _isPointerOverPowerButton;

    /// <summary>
    /// The count to show on the live running display - _completedActionCount itself, except while
    /// Jiggle mode's first jiggle hasn't completed yet (still 0), where this reports 1 instead. By the
    /// time anything renders that one-shot "Jiggling now" report, the first jiggle is already
    /// committed to fire, so "0 jiggles" would just be a technically-true but momentarily-stale number
    /// - this keeps the caption and count consistent with each other from the very first instant. Click
    /// mode has no equivalent: its startup grace period is a real, user-cancellable wait where 0 clicks
    /// so far is still accurate. Never used for the post-stop "Stopped after N" summary, which must
    /// show the real final count.
    /// </summary>
    private int DisplayedActionCount =>
        _runningMode == AutomationMode.Jiggle && _completedActionCount == 0 ? 1 : _completedActionCount;

    /// <summary>DisplayedActionCount, formatted for _runningMode - shorthand for the repeated
    /// FormatActionCount(DisplayedActionCount, _runningMode) call at every count-display site.</summary>
    private string DisplayedActionCountText => FormatActionCount(DisplayedActionCount, _runningMode);

    // The configured interval for this run (before any per-cycle randomize-interval draw - see
    // MouseAutomationEngine.GetEffectiveInterval), captured once at Start() time same as _runningMode.
    // StartImminentBlinkIfNeeded reads this to suppress the Imminent blink on short intervals (see
    // ImminentBlinkMinimumInterval).
    private TimeSpan _runningInterval;

    // Whether the pointer is currently held down over PowerToggleButton or PowerToggleAlternateButton.
    // GetAlternateButtonForeground reads this for the alternate button's press-tinted text color (both
    // Paused and Starting states). Set by PowerToggleButton_PointerPressed, cleared by
    // PointerReleased/Canceled/CaptureLost so an interrupted press can never leave it stuck true.
    private bool _isPowerButtonPressed;

    // Countdown display mode's cache of the engine's most recent report - UpdatePowerButtonRunningDisplay
    // (via UpdatePowerButtonCountdownDisplay) needs these outside of a fresh Engine_StatusChanged tick
    // too (e.g. a hover enter/exit with no new report in between). _lastEngineStatusKind drives the
    // Paused/Starting/JiggleStarting branches; _lastEngineStatusRemaining is the pause's resume
    // countdown once StillnessDisplayThreshold has passed (null before that and on fresh movement).
    // Reset to Off/"Off"/null at the start of every run so a leftover Paused state can't leak in.
    private StatusKind _lastEngineStatusKind = StatusKind.Off;
    private string _lastEngineStatusText = "Off";
    private TimeSpan? _lastEngineStatusRemaining;

    // Global Start/Stop hotkey (F6 by default, configurable in Settings). RegisterHotKey calls live
    // here (see GlobalHotkeyService, InitializeGlobalHotkey, HotkeyService_HotkeyPressed) since they
    // need the WndProc subclass installed on this window; recording a new combination is
    // SettingsPanel's concern. _startTriggeredByHotkey is set just before HotkeyService_HotkeyPressed
    // programmatically checks PowerToggleButton, and consumed at the top of PowerToggleButton_Checked
    // to skip the normal startup countdown and fire the first action immediately (a real button click
    // still uses the countdown). _startTriggeredByTrayAutoClick is the same idea for the tray's "Start
    // Auto Click" item - only for Click mode, since Jiggle already skips the startup grace regardless.
    private readonly GlobalHotkeyService _hotkeyService = new();
    private bool _startTriggeredByHotkey;

    // True for the entire duration AutoStopDialog is shown (set before ShowAsync, cleared in a finally
    // after it returns - see AutoStopButton_Click). The global hotkey and tray "Start" items are
    // independent of WinUI's input/focus system, so the dialog being "modal" doesn't stop either from
    // firing while it's open; PowerToggleButton_Checked rejects a start while this is true, so a run
    // can't begin while its own config dialog is still sitting open. Also mirrored into
    // TrayIconService (SetAutoStopDialogOpen) so its "Start" menu items visibly gray out instead of
    // silently doing nothing when picked.
    private bool _isAutoStopDialogOpen;
    private bool _startTriggeredByTrayAutoClick;

    // Settings rows (see Controls/SettingsPanel.xaml) hosted permanently inside SettingsHost, within
    // SettingsOverlay. See InitializeSettingsPanel for the event/delegate contract wiring it back up
    // to this window's own window/system-level state.
    private readonly Controls.SettingsPanel _settingsPanel = new();

    // Guards ShowSettingsOverlay/SettingsBackButton_Click against re-entry while the slide
    // animation between them (see AnimatePanelTransition) is still running.
    private bool _isSettingsTransitioning;

    // Working copies of the values being edited while AutoStopDialog is open. Populated fresh from the
    // committed _stopDateTime/_autoStopCount/_stopDuration/_timeStopTime (or sensible defaults) each
    // time the dialog opens, for every mode's controls at once, and updated live as the user interacts
    // with StopDatePicker/StopTimePicker/AutoStopCountBox/the duration NumberBoxes. Only copied into
    // their committed counterparts on OK (ContentDialogResult.Primary - see AutoStopButton_Click);
    // Cancel/Escape/dismissing discards them without touching committed state. _stagedStopTime is
    // shared by both AutoStopPickDateToggle states (Date&amp;time vs. recurring Time).
    private DateTime _stagedStopDate;
    private TimeSpan _stagedStopTime;
    private int _stagedAutoStopCount;
    private int _stagedStopDurationHours;
    private int _stagedStopDurationMinutes;
    private int _stagedStopDurationSeconds;

    // Guards AutoStopSegmentedControl_SelectionChanged against re-entry while the slide animation
    // between two tab containers (see AnimatePanelTransition) is still running - same pattern as
    // _isSettingsTransitioning.
    private bool _isAutoStopTabTransitioning;

    // Set around AutoStopButton_Click's SelectedIndex/IsOn seeding so the dialog's first render lands
    // directly on the right tab with no slide - SelectionChanged only actually fires there when the
    // seeded value differs from whatever the control was left showing from a previous open.
    private bool _isSeedingAutoStopDialog;

    // Whichever tab container is the settled (or in-flight animation's) target, tracked so
    // AutoStopSegmentedControl_SelectionChanged can tell whether a newly clicked tab sits left
    // (reverse) or right of it - SelectedIndex itself has already moved on to the new value by the
    // time that handler runs.
    private int _autoStopVisibleSegmentIndex;

    // Guards AutoStopSegmentedControl_SelectionChanged's own reassignment of SelectedIndex (used to
    // revert a click that landed mid-transition) against recursing back into itself.
    private bool _isRevertingAutoStopTabSelection;

    // The index AutoStopButton_Click most recently seeded AutoStopSegmentedControl.SelectedIndex with -
    // kept separately from _autoStopVisibleSegmentIndex, which the first-open churn below can clobber,
    // so AutoStopSegmentedControl_Loaded has a stable value to reapply.
    private int _autoStopSeedTargetSegmentIndex;

    // toolkit:Segmented only finishes generating its item containers - and, as a side effect, self-heals
    // its own SelectedIndex (bouncing through -1, settling on its last item) - the first time it is ever
    // shown, which races AutoStopButton_Click's synchronous seed. True once AutoStopSegmentedControl_Loaded
    // has reconciled that race, so later dialog opens (which don't re-run container generation) skip it.
    private bool _hasAutoStopSegmentedControlStabilized;

    // Committed (persisted) Auto Stop count preset values, index-aligned with AutoStopCountContainer's
    // three preset ToggleButtons - the source RefreshCountPresetLabels renders onto those buttons, and
    // what SelectCountPresetForEditing seeds _stagedCountPresetValues from. Loaded from
    // AppConfig.AutoStopCountPresets in LoadConfigIntoUi, overwritten only by a successful save
    // (SaveCountPresetEdits).
    private int[] _countPresetValues = { 30, 100, 500 };

    // Non-null while Action count preset editing is active and a specific slot is selected (see
    // SelectCountPresetForEditing). AutoStopCountBox_ValueChanged branches on this: non-null routes a
    // committed edit into _stagedCountPresetValues[index] instead of _stagedAutoStopCount, so
    // in-progress preset edits never leak into the actual configured count.
    // _stagedCountPresetValues is a working copy seeded from _countPresetValues on entering edit mode
    // and only copied back on an explicit save (SaveCountPresetEdits) - an abandoned edit
    // (AbandonCountPresetEditIfActive) just discards it.
    private int? _editingCountPresetIndex;
    private readonly int[] _stagedCountPresetValues = new int[3];

    // AutoStopCountBox's real, non-preset-editing value at the moment EnterCountPresetEditMode ran -
    // restored into the box (FinishCountPresetEditMode) once editing ends, whether saved or abandoned.
    private int _preEditAutoStopCount;

    // Duration counterparts of the five Count fields above - same rationale, split into
    // Hours/Minutes/Seconds since NumberBox.Value is a plain double per field.
    private int[] _durationPresetHours = { 0, 4, 24 };
    private int[] _durationPresetMinutes = { 30, 0, 0 };
    private int[] _durationPresetSeconds = { 0, 0, 0 };
    private int? _editingDurationPresetIndex;
    private readonly int[] _stagedDurationPresetHours = new int[3];
    private readonly int[] _stagedDurationPresetMinutes = new int[3];
    private readonly int[] _stagedDurationPresetSeconds = new int[3];
    private int _preEditDurationHours;
    private int _preEditDurationMinutes;
    private int _preEditDurationSeconds;

    // Set once in the constructor, after InitializeComponent so the named elements already exist -
    // index-aligned with each row's own committed/staged preset arrays above. Read by both preset
    // rows' Click handlers and by every Select/Refresh/Finish*PresetEditMode helper.
    private ToggleButton[] _countPresetButtons = null!;
    private ToggleButton[] _durationPresetButtons = null!;

    // Set just before Engine_AutoStopped or a hotkey-triggered stop programmatically flips
    // PowerToggleButton off, cleared by PowerToggleButton_Unchecked. Without this, such a stop landing
    // within MouseAutomationEngine.WasFirstClickJustInjected's detection window could be misattributed
    // as the run's first click self-stopping it, wrongly showing the shrug status.
    private bool _isProgrammaticToggleOff;

    // Set just before Close() is called from AppWindow_Closing's "Close anyway" path, so the
    // Closing event that call raises recognizes this as the already-confirmed close and lets it
    // through instead of cancelling and showing CloseConfirmationDialog a second time.
    private bool _isClosingConfirmed;

    // Set just before PowerToggleButton_Checked reverts an invalid start (Auto Stop enabled but never
    // configured, or enabled with a DateTime already passed) by setting IsChecked back to false, so
    // the PowerToggleButton_Unchecked that raises knows to no-op instead of overwriting the error text
    // or running normal stop side effects for a run that never started.
    private bool _isRejectingInvalidStart;

    // "Close to system tray" setting (SettingsPanel's CloseToTrayToggle, persisted via ConfigService) -
    // mirrors the toggle's IsOn state so AppWindow_Closing can read it synchronously. See
    // TrayIconService for the Shell_NotifyIcon plumbing, and TrayIconService_ExitRequested for how
    // "Exit" from the tray menu reuses CloseConfirmationDialog.
    private readonly TrayIconService _trayIconService = new();
    private bool _closeToTray;

    /// <summary>
    /// Set true from the constructor when LaunchWindowMode is "Tray" - checked by App.xaml.cs's
    /// OnLaunched, which skips its usual Activate() call in that case so the window never flashes
    /// visible before landing in the same hidden-in-tray state AppWindow_Closing's "close to tray"
    /// path produces.
    /// </summary>
    public bool StartHiddenInTray { get; private set; }

    /// <summary>
    /// Set true from the constructor when LaunchWindowMode is "Minimized" - checked by App.xaml.cs's
    /// OnLaunched, which skips its usual Activate() call in that case (same as StartHiddenInTray) so
    /// the window never flashes restored before landing in the already-minimized state the
    /// constructor's ShowWindow(SW_SHOWMINNOACTIVE) call put it in.
    /// </summary>
    public bool StartMinimized { get; private set; }

    // "Show timer in taskbar" setting (SettingsPanel's ShowTaskbarProgressToggle) - mirrors the
    // ITaskbarList3-driven progress bar overlaid on the taskbar icon while automation runs (see
    // Services/TaskbarProgressService). _lastTaskbarProgress holds the most recent progress reported
    // by the engine so a "Paused" report with no countdown of its own can still repaint the bar at
    // wherever it already was, instead of resetting it to 0.
    private readonly TaskbarProgressService _taskbarProgressService = new();
    private bool _showTaskbarProgress;
    private double _lastTaskbarProgress;

    public MainWindow()
    {
        InitializeComponent();

        // ButtonBase marks PointerPressed/PointerReleased as handled internally as part of its own
        // press/click handling, so a plain XAML PointerPressed="..." attribute on PowerToggleButton
        // never fires. AddHandler with handledEventsToo: true is the way around that.
        PowerToggleButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PowerToggleButton_PointerPressed), handledEventsToo: true);
        PowerToggleButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PowerToggleButton_PointerReleased), handledEventsToo: true);
        PowerToggleButton.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(PowerToggleButton_PointerCanceled), handledEventsToo: true);
        PowerToggleButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PowerToggleButton_PointerCaptureLost), handledEventsToo: true);

        // Same press-handling caveat applies to PowerToggleAlternateButton - reuses the same handler
        // methods since they only read/write the shared _isPowerButtonPressed flag.
        PowerToggleAlternateButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PowerToggleButton_PointerPressed), handledEventsToo: true);
        PowerToggleAlternateButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PowerToggleButton_PointerReleased), handledEventsToo: true);
        PowerToggleAlternateButton.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(PowerToggleButton_PointerCanceled), handledEventsToo: true);
        PowerToggleAlternateButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PowerToggleButton_PointerCaptureLost), handledEventsToo: true);

        // Set here rather than as a XAML Maximum attribute: WinUI's XAML compiler round-trips large
        // double attributes through a 32-bit float in its compiled (XBF) encoding, and 99999959 (above
        // float32's exact-integer range of 2^24) silently became 99999960 at runtime. A plain C#
        // assignment has no such precision loss. Set before LoadConfigIntoUi reads it.
        MinutesBox.Maximum = 99999959d;

        // Same XBF float32 precision issue as above - 99999999 is also past 2^24.
        AutoStopCountBox.Maximum = 99999999d;

        // Index-aligned lookup tables for the Auto Stop preset rows - built here, after
        // InitializeComponent so the named elements already exist.
        _countPresetButtons = new[] { AutoStopCountPreset1Button, AutoStopCountPreset2Button, AutoStopCountPreset3Button };
        _durationPresetButtons = new[] { AutoStopDurationPreset1Button, AutoStopDurationPreset2Button, AutoStopDurationPreset3Button };

        // Index-aligned lookup table for IntervalOptionsFlyout's up-to-6 preset slots (see
        // RefreshIntervalPresetFlyoutButtons).
        _intervalPresetFlyoutButtons = new[]
        {
            IntervalPresetFlyoutButton1, IntervalPresetFlyoutButton2, IntervalPresetFlyoutButton3,
            IntervalPresetFlyoutButton4, IntervalPresetFlyoutButton5, IntervalPresetFlyoutButton6,
        };

        // Same shape as _intervalPresetFlyoutButtons above, for each preset button's right-click
        // ContextFlyout "Use preset" MenuFlyoutItem - RefreshIntervalPresetFlyoutButtons keeps each of
        // these IsEnabled in sync with !_isIntervalPresetEditArmed (disabled while customizing presets).
        _intervalPresetUseMenuItems = new[]
        {
            IntervalPresetUseMenuItem1, IntervalPresetUseMenuItem2, IntervalPresetUseMenuItem3,
            IntervalPresetUseMenuItem4, IntervalPresetUseMenuItem5, IntervalPresetUseMenuItem6,
        };

        // Kept in sync with SingleInstanceService.MainWindowTitle rather than a separate literal - that's
        // the exact string FindWindow searches for from a second-instance process, and it differs for
        // Debug builds so a Debug build can run alongside an installed Release build.
        Title = SingleInstanceService.MainWindowTitle;

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

        // AutoStopCountBox ("After a number of clicks/jiggles") gets the same digits-only filter and
        // MaxLength cap as the interval fields - no decimal point (always a whole count), 8 matches its
        // Maximum's digit count (99999999). SuppressClearButton because SpinButtonPlacementMode="Hidden"
        // makes the built-in clear "X" inert in that configuration. rejectLeadingZero: true since
        // Minimum="1" means 0 is never a valid count.
        SetInputBoxMaxLength(AutoStopCountBox, maxLength: 8, allowDecimalPoint: false, rejectLeadingZero: true);
        SuppressClearButton(AutoStopCountBox);
        HookCountPresetLiveSync();

        // The "After a period of time" duration NumberBoxes get the full AdvancedIntervalRow-style live
        // typing treatment (see HookAutoStopDurationBoxLiveSync), not just SetInputBoxMaxLength's
        // simpler filter, so they behave exactly like HoursBox/AdvancedMinutesBox/AdvancedSecondsBox.
        // maxLength matches each box's Maximum digit count (999999/59/59); Minutes/Seconds also get the
        // live 59-ceiling clamp since they share that real bound.
        HookAutoStopDurationBoxLiveSync(AutoStopDurationHoursBox, maxLength: 6);
        HookAutoStopDurationBoxLiveSync(AutoStopDurationMinutesBox, maxLength: 2, liveClampToFiftyNine: true);
        HookAutoStopDurationBoxLiveSync(AutoStopDurationSecondsBox, maxLength: 2, liveClampToFiftyNine: true);
        HookDurationPresetLiveSync(AutoStopDurationHoursBox, _stagedDurationPresetHours);
        HookDurationPresetLiveSync(AutoStopDurationMinutesBox, _stagedDurationPresetMinutes);
        HookDurationPresetLiveSync(AutoStopDurationSecondsBox, _stagedDurationPresetSeconds);
        UpdateTitleBarCaptionSpacer();

        // MouseUtil.csproj's ApplicationIcon only embeds this icon into the exe's PE resources (File
        // Explorer/shortcuts/pinned-taskbar icon); WinUI 3's Window/AppWindow has no equivalent
        // auto-binding for the running window's own icon, so without this call Alt-Tab/Task
        // View/taskbar previews fall back to a generic default. Reuses the same icon as the tray.
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

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateRandomizeIntervalIndicator();

            // Native ToggleSwitches disabled while automation is running bake their Off-track color
            // once, at the moment they're disabled - it goes stale on a later theme change and needs
            // forcing back to life here (see RefreshDisabledToggleSwitchesTheme).
            _settingsPanel.RefreshDisabledToggleSwitchesTheme();
            SettingsPanel.RefreshToggleSwitchDisabledVisual(AutoStopToggle);
            SettingsPanel.RefreshToggleSwitchDisabledVisual(RandomizeIntervalToggle);
            SettingsPanel.RefreshToggleSwitchDisabledVisual(IntervalDisplayToggle);
        };

        // AutoStopCountBox and the three duration NumberBoxes can be left with a stuck, invisible glyph
        // layout the first time this dialog is shown (see RefreshStaleNumberBoxTextLayout). Opened
        // fires only once the dialog is actually composed/shown, so this is the right place to fix it.
        // All four are refreshed unconditionally regardless of which mode is visible - refreshing a
        // Collapsed box is a harmless no-op.
        AutoStopDialog.Opened += (_, _) =>
        {
            RefreshStaleNumberBoxTextLayout(AutoStopCountBox);
            RefreshStaleNumberBoxTextLayout(AutoStopDurationHoursBox);
            RefreshStaleNumberBoxTextLayout(AutoStopDurationMinutesBox);
            RefreshStaleNumberBoxTextLayout(AutoStopDurationSecondsBox);
        };

        // Sets RandomizeIntervalToggle's initial AutomationProperties.Name/ToolTip from
        // _isRandomizeIntervalEnabled's actual value (restored by LoadConfigIntoUi above), rather than
        // relying on the XAML defaults happening to already match it.
        UpdateRandomizeIntervalIndicator();

        // "Start automatically" (SettingsPanel's "App launch behavior" expander) applies on every
        // launch, not just ones Windows triggered. Mode is already correct here (LoadConfigIntoUi set
        // _isJiggleModeSelected), so this just toggles the button and reuses
        // PowerToggleButton_Checked's existing start logic.
        if (_settingsPanel.RunAutomationOnLaunch)
        {
            PowerToggleButton.IsChecked = true;
        }

        // "Launch window" (SettingsPanel's "App launch behavior" expander) - same "applies on every
        // launch" reasoning as RunAutomationOnLaunch above. Both cases below set StartMinimized/
        // StartHiddenInTray so App.xaml.cs skips its usual Activate() call - calling Activate() on a
        // window already set to minimized/hidden would flash it visible/restored for a frame first.
        // - "Minimized": ShowWindow(SW_SHOWMINNOACTIVE) puts the window in the taskbar-minimized state
        //   directly, without activating it.
        // - "Tray": hide instead, reusing the exact same hidden state AppWindow_Closing's "close to
        //   tray" path produces.
        switch (_settingsPanel.LaunchWindowMode)
        {
            case "Minimized":
                StartMinimized = true;
                NativeMethods.ShowWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id), NativeMethods.SW_SHOWMINNOACTIVE);
                break;
            case "Tray":
                StartHiddenInTray = true;
                AppWindow.Hide();
                break;
        }
    }

    /// <summary>
    /// Subclasses this window's WndProc (see GlobalHotkeyService) and registers the hotkey currently
    /// persisted in config. If registration fails (another app already owns that combination), the
    /// hotkey simply doesn't fire until the user picks a different one in Settings.
    /// </summary>
    private void InitializeGlobalHotkey()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        _hotkeyService.AttachToWindow(hwnd);

        var config = ConfigService.Load();
        _hotkeyService.TryRegister(config.HotkeyModifiers, config.HotkeyKey);
        _hotkeyService.HotkeyPressed += HotkeyService_HotkeyPressed;

        // Single-instance enforcement (see App.OnLaunched/Services/SingleInstanceService): a second
        // launch attempt posts this message to bring this window to the foreground instead of opening
        // a duplicate. Reuses the WndProc subclass GlobalHotkeyService already installed for WM_HOTKEY.
        _hotkeyService.RegisterMessageHandler(SingleInstanceService.ShowWindowMessageId, ActivateAndBringToForeground);
    }

    /// <summary>
    /// Wires the settings panel into this window and installs it into SettingsHost, inside
    /// SettingsOverlay, as its single, permanent home.
    /// </summary>
    private void InitializeSettingsPanel()
    {
        _settingsPanel.TryRegisterHotkey = (modifiers, key) => _hotkeyService.TryRegister(modifiers, key);
        _settingsPanel.UnregisterHotkey = () => _hotkeyService.Unregister();

        _settingsPanel.ThemeSelectionChanged += (_, theme) => ApplyTheme(theme);
        _settingsPanel.BackdropSelectionChanged += (_, backdrop) => ApplyBackdrop(backdrop);
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
        _settingsPanel.AboutExpanderExpanded += (_, _) =>
        {
            SettingsScrollViewer.UpdateLayout();
            SettingsScrollViewer.ChangeView(null, SettingsScrollViewer.ScrollableHeight, null);
        };
        _settingsPanel.SettingsExpanderExpanded += (_, expander) => ScrollExpanderIntoView(expander);

        SettingsHost.Children.Add(_settingsPanel);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsOverlay();

    /// <summary>
    /// Opens the Settings overlay and moves focus onto SettingsBackButton. SettingsButton itself needs
    /// no explicit hide - it's a child of MainContentGrid, which already goes fully Collapsed once
    /// Settings is showing.
    /// _settingsPanel.PrepareReflowTransitionsForReopen() runs after AnimatePanelTransition returns,
    /// once SettingsOverlay.Visibility has already flipped to Visible, so it can catch up on
    /// ResetAfterClose's collapses invisibly instead of animating them into view.
    /// </summary>
    private void ShowSettingsOverlay()
    {
        if (_isSettingsTransitioning)
        {
            return;
        }

        // Discard any in-progress interval preset edit before Settings slides in - unlike the Start
        // path (PowerToggleButton_Checked), there's no reason to reject opening Settings mid-edit.
        AbandonIntervalPresetEditIfActive();

        // MainContentGrid's Collapse is deferred to AnimatePanelTransition's completion, once it has
        // actually slid off screen - a Collapsed element can't be animated. _isSettingsTransitioning is
        // owned here (not inside AnimatePanelTransition itself), same as _isAutoStopTabTransitioning is
        // owned by AutoStopSegmentedControl_SelectionChanged - the helper is shared by two unrelated
        // transitions, so each guard flag belongs to its own caller.
        _isSettingsTransitioning = true;
        AnimatePanelTransition(outgoing: MainContentGrid, incoming: SettingsOverlay, reverse: false,
            onCompleted: () =>
            {
                _isSettingsTransitioning = false;
                SettingsBackButton.Focus(FocusState.Programmatic);
            });

        _settingsPanel.PrepareReflowTransitionsForReopen();
    }

    /// <summary>
    /// Reverses ShowSettingsOverlay: slides the overlay back out. UpdateModeIndicators() in
    /// onCompleted re-syncs ModeSegmentedControl.SelectedIndex to _isJiggleModeSelected once the
    /// slide-back finishes, guarding against drift. Resets SettingsScrollViewer to the top and calls
    /// _settingsPanel.ResetAfterClose() (collapses every expander, clears the startup-task error
    /// banner) after a 300ms delay matching the slide duration, so the reset isn't visible mid-slide.
    /// </summary>
    private async void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSettingsTransitioning)
        {
            return;
        }

        _settingsPanel.HandleHostClosing();

        _isSettingsTransitioning = true;
        AnimatePanelTransition(outgoing: SettingsOverlay, incoming: MainContentGrid, reverse: true,
            onCompleted: () =>
            {
                _isSettingsTransitioning = false;
                UpdateModeIndicators();
            });

        await Task.Delay(300);
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        _settingsPanel.ResetAfterClose();
    }

    /// <summary>
    /// Reveals `expander` after it expands, via the platform's bring-into-view support:
    /// StartBringIntoView() does the minimal scroll needed to make it visible (a no-op if already
    /// visible). UpdateLayout() first forces the just-expanded content's new height to be reflected
    /// before StartBringIntoView measures it.
    /// </summary>
    private void ScrollExpanderIntoView(FrameworkElement expander)
    {
        SettingsScrollViewer.UpdateLayout();
        expander.StartBringIntoView();
    }

    /// <summary>
    /// Slides `outgoing` off screen while sliding `incoming` into place, via the Composition API
    /// directly (Visual.Translation/Opacity) rather than a Storyboard, since MainWindow has no
    /// Frame/Page to navigate. reverse=false is Main -> Settings (incoming from the right); reverse=true
    /// mirrors it.
    /// Animates the Translation facade, not Offset - Offset is the same property XAML's layout/Arrange
    /// writes, so animating it directly races Arrange (most visibly the first time a panel is shown,
    /// coming out of Collapsed). Translation composes additively on top of Offset with no race.
    /// Both elements are forced Visible for the animation's duration since a Collapsed element is
    /// excluded from layout; `outgoing` only Collapses again once its exit animation finishes.
    /// IsHitTestVisible is dropped on both so nothing mid-slide can be clicked or tabbed into.
    /// Slide distance is `outgoing`'s own ActualWidth, not `incoming`'s - `incoming` is still Collapsed
    /// at this point (ActualWidth reads 0 until the next layout pass, which hasn't happened yet), while
    /// `outgoing` is already on screen with a real width. For Main/Settings, both fill RootGrid, so this
    /// is unchanged from before; it's what keeps the Auto Stop dialog's tab slide sized to the dialog
    /// (~308px) instead of the full main window.
    /// </summary>
    private void AnimatePanelTransition(FrameworkElement outgoing, FrameworkElement incoming, bool reverse, Action? onCompleted = null)
    {
        var distance = (float)(outgoing.ActualWidth > 0 ? outgoing.ActualWidth
            : RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : AppWindow.Size.Width);
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

                onCompleted?.Invoke();
            });
        };
        batch.End();
    }

    /// <summary>
    /// Brings this window to the foreground regardless of its current state - restores it if minimized,
    /// shows it if hidden, then forces it to the front. Called when a second launch attempt signals
    /// this instance via the message registered above.
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
    /// Wires up TrayIconService: left-click/"Show MouseUtil" restores the window, while "Exit" goes
    /// through TrayIconService_ExitRequested to run the same "automation still running?" confirmation
    /// as AppWindow_Closing. Finishes with UpdateTrayIconVisibility(), which shows the icon immediately
    /// if _closeToTray (set from persisted config by LoadConfigIntoUi) is on.
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
    /// inactive). Forces the mode selector to the requested mode (via SetSelectedMode) then starts
    /// automation by toggling PowerToggleButton exactly as a real click would, reusing
    /// PowerToggleButton_Checked's existing start logic. The _engine.IsRunning guard is defensive.
    /// For Click mode, also arms _startTriggeredByTrayAutoClick so PowerToggleButton_Checked skips the
    /// startup countdown and fires the first click immediately, matching the global hotkey's behavior.
    /// Jiggle is left alone since it already skips the startup grace unconditionally.
    /// Does nothing while AutoStopDialog is open - PowerToggleButton_Checked itself rejects and reverts
    /// an IsChecked = true set while that's true, at its own top, before any real Start logic runs.
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
    /// Handles "Stop" from the tray context menu (only reachable while running). Reuses
    /// PowerToggleButton_Unchecked's stop logic by toggling PowerToggleButton off.
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
    /// Handles "Pause on movement" from the tray context menu (only reachable while inactive). Flips
    /// SettingsPanel's PauseOnMovementToggle itself (via TogglePauseOnMovement) rather than writing to
    /// AppConfig directly, so SettingsPanel's own Toggled handler stays the single place that persists
    /// the setting.
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
    /// Shows or hides the tray icon to match _closeToTray - called at startup and again every time
    /// CloseToTrayToggle changes, so the icon appears/disappears immediately.
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
    /// Handles "Exit" from the tray icon's context menu: restores/activates the window first (so the
    /// user sees where CloseConfirmationDialog, if it appears, is coming from), then applies the same
    /// "automation still running?" guard AppWindow_Closing uses for a normal close.
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
    /// Fires on the UI thread (WM_HOTKEY arrives via the subclassed WndProc, which already runs on it).
    /// Toggles PowerToggleButton exactly as a real click would, reusing
    /// PowerToggleButton_Checked/_Unchecked's Start/Stop logic - except Checked consults
    /// _startTriggeredByHotkey to skip the startup countdown and perform the first action immediately.
    /// _startTriggeredByHotkey is only set on the path about to raise Checked (willStart); setting it
    /// unconditionally, including on Stop, would leave it true to wrongly skip the countdown on a later,
    /// unrelated start (a hotkey-triggered stop raises Unchecked, which never consumes it).
    /// Setting IsChecked = true while AutoStopDialog is open does not actually start anything -
    /// PowerToggleButton_Checked itself rejects and reverts that at its own top.
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
            // This stop sets IsChecked directly with no click involved, so it must be excluded from
            // the shrug-status check (see _isProgrammaticToggleOff).
            _isProgrammaticToggleOff = true;
        }

        PowerToggleButton.IsChecked = willStart;
    }

    /// <summary>
    /// Sizes the trailing spacer column in AppTitleBarGrid to match the system's reserved caption
    /// button area (min/max/close), which ExtendsContentIntoTitleBar overlays on top of our content.
    /// Without this, the ModeSelectorBar can render partly underneath the caption buttons. RightInset
    /// is reported in physical pixels, converted to DIPs using the window's current DPI.
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
    /// 1. _isClosingConfirmed is set - this is the second Closing raised by our own Close() call, after
    ///    the user already confirmed - let it through normally.
    /// 2. _closeToTray is on - cancel the close and hide the window instead of exiting; no confirmation
    ///    needed since automation just keeps running in the tray.
    /// 3. Otherwise: if automation is running, cancel and show CloseConfirmationDialog, only proceeding
    ///    to a real Close() if confirmed; if not running, let the close through normally.
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
    /// Disables resizing and maximize entirely (IsMaximizable also blocks double-click-title-bar and
    /// Win+Up), so the window stays fixed at the size SizeWindow() sets programmatically.
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
        RandomizeIntervalToggle.IsOn = config.RandomizeInterval;

        // SettingsPanel already loaded its own persisted state when constructed in
        // InitializeSettingsPanel, called just before this method - read its mirrors rather than
        // re-parsing config a second time.
        _closeToTray = _settingsPanel.CloseToTray;
        _showTaskbarProgress = _settingsPanel.ShowTaskbarProgress;

        // Applies the persisted Advanced-interval-display setting - called after MinutesBox/SecondsBox
        // above so PopulateAdvancedIntervalFieldsFromBasic reads the just-loaded values.
        UpdateAdvancedIntervalDisplayMode();

        ApplyTheme(config.Theme);
        ApplyBackdrop(config.Backdrop);

        // _settingsPanel.PreferredMode ("LastUsed" by default) can force a specific mode at launch
        // regardless of LastMode (see SettingsPanel's "Preferred mode" dropdown).
        _isJiggleModeSelected = _settingsPanel.PreferredMode switch
        {
            "Click" => false,
            "Jiggle" => true,
            _ => config.LastMode == "Jiggle"
        };
        UpdateModeIndicators();

        _lastConfiguredAutoStopMode = Enum.TryParse<AutoStopMode>(config.AutoStopMode, out var savedAutoStopMode) ? savedAutoStopMode : AutoStopMode.None;
        _autoStopCount = config.AutoStopCount;

        // Duration/Time committed values - loaded unconditionally alongside _autoStopCount, since they
        // stay meaningful regardless of which Auto Stop mode is active (which always starts back at
        // None each session). AppConfig's defaults (2h 0m 0s / 17:00) apply automatically the first
        // time this ever runs, via ConfigService.Load's own fallback when config.json doesn't exist.
        _stopDuration = new TimeSpan(config.AutoStopDurationHours, config.AutoStopDurationMinutes, config.AutoStopDurationSeconds);
        _timeStopTime = new TimeSpan(config.AutoStopTimeHour, config.AutoStopTimeMinute, 0);

        _autoStopMode = AutoStopMode.None;
        UpdateAutoStopButtonLabel();
        SetStopControlsEnabled(false);

        // Auto Stop preset row values, rendered onto each row's three preset ToggleButtons.
        // LoadPresetArray falls back to the hardcoded defaults if a hand-edited/corrupted config.json
        // doesn't have exactly 3 entries for one of these arrays.
        _countPresetValues = LoadPresetArray(config.AutoStopCountPresets, new[] { 30, 100, 500 });
        _durationPresetHours = LoadPresetArray(config.AutoStopDurationPresetHours, new[] { 0, 4, 24 });
        _durationPresetMinutes = LoadPresetArray(config.AutoStopDurationPresetMinutes, new[] { 30, 0, 0 });
        _durationPresetSeconds = LoadPresetArray(config.AutoStopDurationPresetSeconds, new[] { 0, 0, 0 });
        RefreshCountPresetLabels();
        RefreshDurationPresetLabels();

        // Loads the Interval card's presets and renders them onto the flyout's apply buttons.
        LoadIntervalPresets(config);

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

    /// <summary>
    /// Sets the window's SystemBackdrop material from SettingsPanel's "Backdrop" selection. Mica/Mica
    /// Alt/Acrylic are all real, untinted backdrops, so RootGrid.Background is explicitly cleared for
    /// them so the material shows through undisturbed.
    /// </summary>
    private void ApplyBackdrop(string preference)
    {
        switch (preference)
        {
            case "MicaAlt":
                RootGrid.Background = null;
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                break;
            case "Acrylic":
                RootGrid.Background = null;
                SystemBackdrop = new DesktopAcrylicBackdrop();
                break;
            default:
                RootGrid.Background = null;
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                break;
        }
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

        // A live OS theme flip (via UiSettings_ColorValuesChanged, can fire mid-run while "Follow
        // system" is selected) needs to immediately refresh Countdown mode's paused-button colors
        // instead of waiting for the next engine tick. Safe to call unconditionally - no-ops if the
        // button isn't currently checked.
        UpdatePowerButtonRunningDisplay();
    }

    /// <summary>
    /// UISettings.ColorValuesChanged fires for a live OS theme flip - only relevant while "System" is
    /// selected, since an explicit Light/Dark choice already has its own colors from ApplyTheme. Fires
    /// on a non-UI thread, so must marshal back before touching the title bar / XAML tree.
    /// </summary>
    private void UiSettings_ColorValuesChanged(UISettings sender, object args)
    {
        if (_themePreference == "System")
        {
            DispatcherQueue.TryEnqueue(UpdateTitleBarButtonColors);
        }
    }

    /// <summary>
    /// Forces a genuine glyph-layout repaint of one NumberBox known to render invisible/stale text the
    /// first time it's actually shown: AutoStopCountBox (inside AutoStopDialog, a ContentDialog with no
    /// XamlRoot until first shown) and the four Advanced interval fields (inside AdvancedIntervalRow,
    /// Visibility="Collapsed" until the user enables it).
    ///
    /// Root cause: both boxes get box.ApplyTemplate() forced eagerly in the constructor while their
    /// container is still Collapsed / has no XamlRoot, so the inner "InputBox" TextBox's text layout is
    /// computed for the first time at zero available width. Neither a Visibility toggle nor
    /// detaching/reattaching the control forces that cached zero-width layout to recompute - only an
    /// actual Text content change does, and it must happen after the real Arrange pass (a Value write in
    /// the same dispatch as the Visibility flip is too early).
    ///
    /// Fix: round-trip Text after the real Arrange pass has happened - see this method's two callers
    /// (AdvancedIntervalRow's deferred call in UpdateAdvancedIntervalDisplayMode; AutoStopDialog's
    /// Opened event).
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
        // Relies entirely on Segmented's own default Disabled visual state - no custom dimming/restyling.
        ModeSegmentedControl.IsEnabled = enabled;
        MinutesBox.IsEnabled = enabled;
        SecondsBox.IsEnabled = enabled;
        UpdateCompactSpinButtonIndicatorOpacity(MinutesBox, enabled);
        UpdateCompactSpinButtonIndicatorOpacity(SecondsBox, enabled);
        AutoStopToggle.IsEnabled = enabled;
        SetStopControlsEnabled(enabled && AutoStopToggle.IsOn);

        // IntervalCaptionTextBlock/AutoStopCaption are DimmableLabel controls (see
        // Controls/DimmableLabel.cs) - setting IsEnabled drives their Normal/Disabled VisualState
        // transition declaratively.
        IntervalCaptionTextBlock.IsEnabled = enabled;
        AutoStopCaption.IsEnabled = enabled;

        // RandomizeIntervalIndicatorIcon is a plain FontIcon (IconElement, not Control) - it has no
        // IsEnabled property or built-in Disabled VisualState, so its dimming is driven by hand here.
        RandomizeIntervalIndicatorIcon.Opacity = enabled ? 1 : 0.6;

        // Locked while running so the randomize-interval behavior can't change out from under an
        // in-progress run. RandomizeIntervalToggle's built-in Enabled/Disabled visual states already
        // dim it appropriately while locked. UpdateRandomizeIntervalIndicator is still called below,
        // after IsEnabled updates, but only to refresh AutomationProperties.Name/ToolTip.
        RandomizeIntervalToggle.IsEnabled = enabled;
        RandomizeIntervalLabel.IsEnabled = enabled;
        RandomizeIntervalOptionIcon.Opacity = enabled ? 1 : 0.6;
        UpdateRandomizeIntervalIndicator();

        // IntervalOptionsButton itself (the flyout trigger, not just its flyout's contents) - locked
        // for the same reason as everything inside IntervalOptionsFlyout above. Its own default Button
        // Disabled visual state only dims opacity/foreground, it doesn't touch layout, so the header
        // row's fixed 16x16 footprint (see this button's own XAML comment) is unaffected.
        IntervalOptionsButton.IsEnabled = enabled;

        // Preset apply buttons - locked while a run is in progress, since applying one would change
        // the live interval out from under it. Edit/Delete are locked the same way, for consistency.
        foreach (var button in _intervalPresetFlyoutButtons)
        {
            button.IsEnabled = enabled;
        }

        IntervalPresetsEditButton.IsEnabled = enabled;
        IntervalPresetsDeleteButton.IsEnabled = enabled;

        // Undo/Restore defaults - same locked-while-running treatment, ANDed with their own usual
        // enabled condition (non-empty undo stack / not already at defaults) so both stay correct
        // regardless of run state.
        IntervalPresetsUndoButton.IsEnabled = enabled && _intervalPresetUndoStack.Count > 0;
        IntervalPresetsRestoreDefaultsButton.IsEnabled = enabled && !IntervalPresetsMatchDefaults();

        // The four Hours/Minutes/Seconds/Milliseconds fields get the same locked-while-running
        // treatment as MinutesBox/SecondsBox - values can't change out from under an in-progress run.
        HoursBox.IsEnabled = enabled;
        AdvancedMinutesBox.IsEnabled = enabled;
        AdvancedSecondsBox.IsEnabled = enabled;
        MillisecondsBox.IsEnabled = enabled;

        // IntervalDisplayToggle mirrors SettingsPanel's "Interval display" RadioButtons (see
        // IntervalDisplayToggle_Toggled) - locked the same way, independently.
        IntervalDisplayToggle.IsEnabled = enabled;
        FullIntervalDisplayLabel.IsEnabled = enabled;
        FullIntervalDisplayOptionIcon.Opacity = enabled ? 1 : 0.6;

        // Hotkey/ShowActionCounter/PauseOnMovement (and their DimmableLabel captions) live in
        // SettingsPanel now.
        _settingsPanel.SetInputsEnabled(enabled);
    }

    private void SetStopControlsEnabled(bool enabled)
    {
        AutoStopButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Central place to set StatusTextBlock's text/tone/opacity together, including the "¯\_(ツ)_/¯"
    /// shrug (see PowerToggleButton_Unchecked) - every status string uses the app's normal font.
    /// StatusTextBlock is a StatusLabel (see Controls/StatusLabel.cs): passing a StatusTone enum
    /// instead of a Brush lets its own ControlTemplate VisualStates apply the actual
    /// {ThemeResource} color, so it stays correctly themed even if the theme changes while a
    /// non-Muted tone is showing. opacity defaults to fully opaque; this is the only place
    /// StatusTextBlock.Opacity is ever touched, so every caller that omits it implicitly resets it.
    /// </summary>
    private void SetStatusText(string text, StatusTone tone, double opacity = 1.0)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Tone = tone;
        StatusTextBlock.Opacity = opacity;
    }

    private void PowerToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        // Consumed unconditionally, before either early-return below, so a stale true from an earlier
        // hotkey press or tray "Start Auto Click" can never leak into a later, genuine button click.
        var skipStartupCountdown = _startTriggeredByHotkey || _startTriggeredByTrayAutoClick;
        _startTriggeredByHotkey = false;
        _startTriggeredByTrayAutoClick = false;

        // Refuse to start while an interval preset is being edited/added on the Interval card. A direct
        // click is already blocked via PowerToggleButton.IsEnabled (see EnterIntervalPresetCardEditMode),
        // but the global hotkey and tray "Start" menu items bypass that, so this must reject outright -
        // the edit is left untouched, not abandoned (AbandonIntervalPresetEditIfActive).
        if (_editingIntervalPresetIndex is not null || _isAddingIntervalPreset)
        {
            _isRejectingInvalidStart = true;
            PowerToggleButton.IsChecked = false;
            return;
        }

        // Refuse to start while AutoStopDialog is open - the global hotkey and tray "Start" menu items
        // are independent of WinUI's input system, so neither is blocked by the dialog being "modal".
        // No status message, since the dialog is covering StatusTextBlock right now anyway. Sets
        // _isRejectingInvalidStart so the resulting Unchecked no-ops instead of running normal stop
        // side effects for a run that never started.
        if (_isAutoStopDialogOpen)
        {
            _isRejectingInvalidStart = true;
            PowerToggleButton.IsChecked = false;
            return;
        }

        if (AutoStopToggle.IsOn)
        {
            // Refuse to start with no actual stop condition, or with a configured stop time already
            // passed - the engine's own IsStopTimeReached would catch the latter too, but surfacing it
            // here avoids a start-then-immediately-stop flash with no explanation.
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

        // MinutesBox/SecondsBox are read regardless of Basic/Advanced display, since the Advanced
        // row's fields live-sync into these two on every keystroke (see HookAdvancedIntervalBoxLiveSync).
        // Refuse to start with a configured 0 interval rather than let the Math.Max clamp below
        // silently coerce it up to MinimumIntervalSeconds.
        var minutes = ReadCommittedOrTypedValue(MinutesBox, fractionDigits: 0);
        var seconds = ReadCommittedOrTypedValue(SecondsBox, fractionDigits: 3);
        if (minutes * 60 + seconds == 0)
        {
            _isRejectingInvalidStart = true;
            SetStatusText("Enter an interval greater than 0.", StatusTone.Accent);
            PowerToggleButton.IsChecked = false;
            return;
        }

        var totalSeconds = Math.Max(MinimumIntervalSeconds, minutes * 60 + seconds);
        var interval = TimeSpan.FromSeconds(totalSeconds);

        DateTime? stopAt = null;
        int? stopAfterActionCount = null;

        // Reset fresh every Start, before the mode switch below (only the Duration/Time branches
        // re-set them), so a previous run's deadline can never leak into a differently-configured one.
        _durationStopDeadline = null;
        _timeStopDeadline = null;

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
            else if (_autoStopMode == AutoStopMode.Duration)
            {
                // MouseAutomationEngine only understands an absolute stopAt, not a duration - resolved
                // fresh here, relative to the actual start moment, rather than at OK-time in
                // AutoStopButton_Click (which would go stale the longer the app sits idle before Start).
                stopAt = DateTime.Now + _stopDuration;

                // Stashed for ApplyEngineStatus's live countdown display only - the engine itself only
                // ever receives stopAt above.
                _durationStopDeadline = stopAt;
            }
            else if (_autoStopMode == AutoStopMode.Time)
            {
                // Resolves to the next actual occurrence of _timeStopTime: today if it hasn't happened
                // yet, otherwise tomorrow.
                var now = DateTime.Now;
                var todayAtTime = now.Date + _timeStopTime;
                stopAt = todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1);

                // Stashed for ApplyEngineStatus's live "Today"/"Tomorrow" target-time display only.
                _timeStopDeadline = stopAt;
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
        // startup grace before this run's first status tick arrives - otherwise a stop-while-paused
        // followed immediately by a fresh start could leave it visible for a frame. Icon/label/
        // automation name reset to their Paused-ready defaults too, so leftover "Resuming in
        // Xs"/"Stop" text can't leak into a fresh run before UpdateAlternateButtonDisplay overwrites it.
        ShowPowerButton(AlternateButtonState.Hidden);
        StopImminentBlink(); // in case a previous run's Countdown-mode Imminent blink was still fading
        PowerToggleAlternateLabel.ClearValue(TextBlock.FontSizeProperty); // in case a previous run shrank it
        PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // in case a previous run shrank it
        PowerToggleAlternateIcon.Glyph = "\uF8AE"; // PauseBold glyph - UpdateAlternateButtonDisplay's own default.
        PowerToggleAlternateLabel.Text = "Paused";
        AutomationProperties.SetName(PowerToggleAlternateButton, "Power, Stop");
        SetStatusText("Off", StatusTone.Muted);

        SetInputsEnabled(false);

        // Everything inside IntervalOptionsFlyout just went disabled above - a real button click gets
        // this closed for free via WinUI's default light-dismiss, but the global hotkey and tray "Start"
        // items produce no pointer event to trigger that, so it's closed explicitly here too.
        IntervalOptionsFlyout.Hide();

        PowerToggleIcon.Glyph = "\uEE95"; // Stop glyph - pressing the Start/Stop button now would stop the engine.
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.Text = "Stop";
        AutomationProperties.SetName(PowerToggleButton, "Power, Stop");

        _engine.Start(mode, interval, stopAt, stopAfterActionCount, _settingsPanel.IsPauseOnMovementActiveForMode(mode), _isRandomizeIntervalEnabled, skipStartupCountdown);
        _trayIconService.UpdateState(isRunning: true, isPaused: false, mode: mode);
    }

    // NumberBox only re-parses typed input into Value on focus loss/Enter, so a hotkey-triggered start
    // (which never moves focus) would otherwise read the last-committed Value and ignore text just
    // typed. NumberBox's Text DP isn't a reliable stand-in either (measured up to 60+ms of lag), so
    // this reads the template's actual "InputBox" TextBox part directly.
    //
    // fractionDigits truncates (floors, never rounds) the parsed value to that many decimal places
    // before clamping to box.Minimum/box.Maximum, mirroring the truncation IntervalBox_ValueChanged
    // applies at commit time, but live for text that was never committed. See TruncateToFractionDigits.
    private static double ReadCommittedOrTypedValue(NumberBox box, int fractionDigits)
    {
        var liveText = FindInputBoxText(box) ?? box.Text;

        // InvariantCulture, not CurrentCulture: on a machine whose Windows region uses ',' as decimal
        // separator and '.' as thousands separator (e.g. de-DE, ro-RO), NumberStyles.Any's
        // AllowThousands silently strips an unrecognized '.' as a thousands separator instead of
        // rejecting it - "32.5" parses as 325, "59.999" as 59999. Only SecondsBox ever has a literal
        // '.' (the rest are digits-only), and HookIntervalCharacterFilter's comma-to-'.' normalization
        // guarantees its Text is always invariant-formatted, so parsing it invariantly is correct
        // regardless of OS locale.
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
    /// decimal places. Shared by every interval-field truncation path so the "truncate, don't round"
    /// rule lives in one place - NumberBox's own NumberFormatter/FractionDigits can't substitute since
    /// it only affects displayed Text, never writes back into .Value, and rounds instead of truncating.
    /// Arithmetic is done in decimal, not double: e.g. 1.001 * 1000 doesn't land exactly on 1001 in
    /// IEEE-754 double (evaluates to 1000.9999999999999), which would wrongly truncate to 1.0.
    /// Converting through decimal erases that binary-representation noise before truncating.
    /// </summary>
    private static double TruncateToFractionDigits(double value, int fractionDigits)
    {
        var scale = (decimal)Math.Pow(10, fractionDigits);
        var truncated = Math.Truncate((decimal)value * scale) / scale;
        return (double)truncated;
    }

    private static string? FindInputBoxText(DependencyObject root) => FindInputBox(root)?.Text;

    // Used to read live, uncommitted text (ReadCommittedOrTypedValue/FindInputBoxText above) and to
    // hook the four Advanced interval NumberBoxes' inner TextBox's live TextChanged event directly (see
    // InitializeAdvancedIntervalLiveSync) - NumberBox.ValueChanged only fires on commit.
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
    /// Dims MinutesBox/SecondsBox's Compact spin-button chevron to match their IsEnabled state - 0.3
    /// rather than the 0.6 used elsewhere, since this glyph reads more prominently at rest and needs to
    /// dim further to look equivalently disabled.
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

        // Shows the shrug only if this run's very first automated click caused this stop (the cursor
        // happened to already be over the button) - never a later self-inflicted click, and never the
        // scheduled-auto-stop/hotkey-stop paths (excluded via _isProgrammaticToggleOff).
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

        // Re-renders from _autoStopMode's current value - a no-op if this run left it untouched, or
        // reflects the "Configure" placeholder if Checked just reset it to None (Auto Stop disabled).
        UpdateAutoStopButtonLabel();

        SetInputsEnabled(true);
        StopImminentBlink(); // Stop pressed mid-blink shouldn't leave PowerToggleLabel faded for "Start".
        // Icon/label flip to Play/"Start" synchronously so PowerToggleButton is ready the instant it
        // becomes visible, whether that's now or once the stop-while-paused transition below finishes.
        PowerToggleIcon.Glyph = "\uE768"; // Play glyph - pressing the Start/Stop button now would start the engine.
        PowerToggleIcon.Visibility = Visibility.Visible;
        PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // in case this run's countdown text had shrunk it
        PowerToggleLabel.Text = "Start";
        AutomationProperties.SetName(PowerToggleButton, "Power, Start");

        // Reveal PowerToggleButton immediately whether or not we were paused - PowerToggleAlternateButton's
        // Paused color matches PowerToggleButton's own Checked background (see
        // GetAlternateButtonBackground/GetAlternateButtonForeground), so there's no color mismatch to mask.
        ShowPowerButton(AlternateButtonState.Hidden);

        if (showShrug)
        {
            SetStatusText(SelfInflictedOffStatusText, StatusTone.Muted);
        }
        else
        {
            // Shows the counter result instead of plain "Off" whenever at least one counted action
            // happened, regardless of "Stop button display"'s toggle/mode state.
            var text = _completedActionCount > 0
                ? $"Stopped after {FormatActionCount(_completedActionCount, _runningMode)}"
                : "Off";
            SetStatusText(text, StatusTone.Muted);
        }
    }

    /// <summary>
    /// PowerToggleAlternateButton's Click handler (both Paused and Starting states) - flips
    /// PowerToggleButton's own IsChecked off, which raises PowerToggleButton_Unchecked and runs the
    /// entire stop path exactly as if PowerToggleButton itself had been unchecked.
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
            // countdown or via the hotkey - it's action #1, not a free kickoff. This is also what
            // flips the button to showing the counter instead of "Stop" once > 0 (see
            // UpdatePowerButtonRunningDisplay).
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
    /// ShowStopButtonDisplayToggle is on AND the radio buttons beneath it are set to Countdown.
    /// </summary>
    private bool IsCountdownDisplayActive =>
        _settingsPanel.IsStopButtonDisplayShown && _settingsPanel.StopButtonDisplay == Controls.StopButtonDisplayMode.Countdown;

    /// <summary>
    /// Refreshes PowerToggleButton's live label/icon/background while the engine is running - the single
    /// place that decides what the button shows, branching on SettingsPanel's ShowStopButtonDisplayToggle/
    /// StopButtonDisplay:
    /// - Toggle off: always plain "Stop" (icon visible) - see UpdatePowerButtonNoneDisplay. Countdown and
    ///   count both move to the status bar instead (see GetStatusBarText).
    /// - Toggle on, Countdown mode: shows the engine's own live countdown text (e.g. "Clicking in 4s") -
    ///   see UpdatePowerButtonCountdownDisplay for the paused/hover special cases.
    /// - Toggle on, Counter mode: "Stop" until the first action fires, then the running
    ///   "{count} click(s)/jiggle(s)" text, yielding back to "Stop" while hovered.
    /// No-ops while the button isn't checked/running.
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

        StopImminentBlink(); // Counter mode has no Imminent blink - guards a mid-run switch away from Countdown.
        if (DisplayedActionCount > 0 && !_isPointerOverPowerButton)
        {
            PowerToggleLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
            PowerToggleIcon.Visibility = Visibility.Collapsed;
            PowerToggleLabel.Text = DisplayedActionCountText;
            return;
        }

        SetStopButtonContent(PowerToggleIcon, PowerToggleLabel);
    }

    /// <summary>
    /// ShowStopButtonDisplayToggle-off half of UpdatePowerButtonRunningDisplay: PowerToggleButton always
    /// shows plain "Stop" with its Stop icon for the entire run - never the click/jiggle count or a
    /// countdown, and never PowerToggleAlternateButton. Both move to the status bar instead (see
    /// GetStatusBarText).
    /// ShowPowerButton(AlternateButtonState.Hidden) is called unconditionally so that turning the
    /// toggle off while PowerToggleAlternateButton is showing (left over from Countdown mode)
    /// immediately reverts to the plain button.
    /// </summary>
    private void UpdatePowerButtonNoneDisplay()
    {
        ShowPowerButton(AlternateButtonState.Hidden);
        StopImminentBlink(); // This state has no Imminent blink - guards a mid-run switch away from Countdown.
        SetStopButtonContent(PowerToggleIcon, PowerToggleLabel);
    }

    /// <summary>
    /// Puts icon/label into the plain waiting "Stop" state shared by every hover override (Countdown
    /// and Counter mode's button, and the Paused/Starting alternate button) and by the toggle-off
    /// display: Stop glyph, icon visible, label text "Stop", font size cleared back to the inherited
    /// base (undoing any prior countdown-mode shrink). Foreground is left untouched - callers that need
    /// a specific color (UpdateAlternateButtonDisplay) apply it separately, before or after this call.
    /// </summary>
    private static void SetStopButtonContent(FontIcon icon, TextBlock label)
    {
        icon.Glyph = "\uEE95"; // Stop glyph
        icon.Visibility = Visibility.Visible;
        label.ClearValue(TextBlock.FontSizeProperty);
        label.Text = "Stop";
    }

    /// <summary>
    /// IsCountdownDisplayActive's half of UpdatePowerButtonRunningDisplay. Delegates entirely to
    /// UpdateAlternateButtonDisplay whenever the engine's last report is StatusKind.Paused
    /// (pause-on-movement) or StatusKind.Starting (Click mode's startup grace) - that method owns
    /// everything about PowerToggleAlternateButton for both. Both checks come before the hover check
    /// since Starting must never yield to hover's "Stop" text.
    /// Otherwise operates on PowerToggleButton: swaps PowerToggleAlternateButton back out instantly if
    /// a resume/startup grace just ended, then falls through to hover/live-countdown. Hovering always
    /// wins and shows the Stop glyph + "Stop"; everything else - including JiggleStarting's one-shot
    /// "Jiggling now" - shows the engine's live status text with the icon collapsed.
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
        // PowerToggleAlternateButton back out instantly if it's still showing from a pause/startup
        // grace that just ended. Idempotent - a no-op once _alternateButtonState is already Hidden.
        if (_alternateButtonState != AlternateButtonState.Hidden)
        {
            ShowPowerButton(AlternateButtonState.Hidden);
        }

        if (_isPointerOverPowerButton)
        {
            StopImminentBlink(); // hovering always shows static "Stop" - it shouldn't blink.
            SetStopButtonContent(PowerToggleIcon, PowerToggleLabel);
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
    /// about PowerToggleAlternateButton whenever the engine's last report is StatusKind.Paused
    /// (pause-on-movement) or StatusKind.Starting (Click mode's startup grace): visibility (swapped in
    /// instantly, no animation), colors, and text/icon content.
    /// Starting returns immediately, bypassing the shared hover-Stop check: hovering during the startup
    /// countdown should keep showing the countdown, not "Stop", since it's brief (max 3s) and is the
    /// point of the state. Just passes _lastEngineStatusText straight through.
    /// Paused: hover-Stop first (shared via _isPointerOverPowerButton), then splits on
    /// _lastEngineStatusRemaining into plain-Paused vs Resuming-in-Xs. The foreground color is computed
    /// once up front and applied uniformly to every branch.
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
            SetStopButtonContent(PowerToggleAlternateIcon, PowerToggleAlternateLabel);
            return;
        }

        // _lastEngineStatusText is already the right text verbatim either way - "Resuming in Xs" (the
        // engine's own countdown formatting) once StillnessDisplayThreshold has passed, plain "Paused"
        // before that. Only the icon/font-size treatment differs between the two.
        PowerToggleAlternateLabel.Text = _lastEngineStatusText;

        if (_lastEngineStatusRemaining.HasValue)
        {
            // A real resume countdown is available - hide the pause icon and shrink the font for long
            // countdowns. Fresh movement resets _lastEngineStatusRemaining back to null, reverting to
            // the plain-Paused icon/font treatment below.
            PowerToggleAlternateIcon.Visibility = Visibility.Collapsed;
            ApplyCountdownFontSize(PowerToggleAlternateLabel, _lastEngineStatusRemaining);
            return;
        }

        PowerToggleAlternateLabel.ClearValue(TextBlock.FontSizeProperty); // always short text here, never shrunk
        PowerToggleAlternateIcon.Glyph = "\uF8AE"; // PauseBold glyph
        PowerToggleAlternateIcon.Visibility = Visibility.Visible;
    }

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
    /// Font size for a countdown string like "Clicking in 23h 14m 06s" - hours can grow arbitrarily
    /// large (HoursBox's Maximum is 1,666,665), long enough to overflow the button's fixed 220px width.
    /// Steps down PowerToggleButton's own FontSize by PowerButtonCountdownFontStepDown once per extra
    /// digit in the hours portion, starting at double digits, capped at PowerButtonMinimumCountdownFontSize
    /// (the 100h+ size). Returns null ("use the base size") when remaining is null or hours is
    /// single-digit, so callers know to ClearValue instead.
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
    /// Pushes a fresh color into PowerToggleAlternateNormalBrush/PointerOverBrush/PressedBrush - the
    /// three named brushes shadowing ButtonBackground/ButtonBackgroundPointerOver/ButtonBackgroundPressed
    /// inside PowerToggleAlternateButton's Resources. All three get the same color (it doesn't vary by
    /// pointer state), since DefaultButtonStyle's native CommonStates Storyboard picks whichever one
    /// currently matches the button's real pointer state. Mutates each brush's Color in place rather
    /// than replacing the brush, so whichever one is showing keeps rendering, just repainted.
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
    // text shaded on top (GetAlternateButtonForeground). Starting reads SuccessBrushSource's plain
    // green, unadjusted, for both. Paused diverges: the background's base color differs by theme (raw
    // SystemAccentColor in Light, AccentFillColorDefaultBrush's Fill variant in Dark - via
    // GetAccentColorForTheme), while the foreground always uses its own separately-tuned live brush
    // (AccentTextFillColorPrimaryBrush) regardless of theme, for legibility as text.
    // SuccessBrushSource must be a live, already-theme-resolved FrameworkElement's Foreground rather
    // than a raw resource-dictionary lookup, since SystemFillColorSuccessBrush is
    // Light/Dark/HighContrast ThemeDictionary-scoped and a flat lookup resolves against
    // Application.RequestedTheme/ActualTheme, not RootGrid's own (this app only ever sets
    // RootGrid.RequestedTheme - see ApplyTheme). A real FrameworkElement's ActualTheme correctly
    // inherits from RootGrid instead.
    // Shading amounts (PausedButtonHoverLightenAmount/PausedButtonPressedDarkenAmount, shared by both
    // states despite the "Paused" name) apply on top of the foreground's per-state base either way.
    // ============================================================================================

    /// <summary>
    /// PowerToggleAlternateButton's translucent background - the state's own base color at
    /// PausedButtonBackgroundOpacity alpha, no opaque layer underneath. Starting always uses
    /// SuccessBrushSource's plain green; Paused reads GetAccentColorForTheme(). Real GPU alpha
    /// transparency is safe here since PowerToggleAlternateButton is its own independent element, so
    /// whatever's behind it is just the plain window/Mica backdrop. No hover/pressed shading applied to
    /// the background itself - that shading lives in the foreground instead (see
    /// GetAlternateButtonForeground); the background stays the same translucent color regardless of
    /// pointer state (see ApplyAlternateButtonBackgroundBrushes).
    /// </summary>
    private Color GetAlternateButtonBackground(AlternateButtonState state)
    {
        var tintedColor = state == AlternateButtonState.Paused
            ? GetAccentColorForTheme()
            : ((SolidColorBrush)SuccessBrushSource.Foreground).Color;
        return Color.FromArgb((byte)Math.Round(255 * PausedButtonBackgroundOpacity), tintedColor.R, tintedColor.G, tintedColor.B);
    }

    /// <summary>
    /// Shared per-theme Windows accent-color lookup used by GetAlternateButtonBackground's Paused tint.
    /// Light reads Application.Current.Resources["SystemAccentColor"] directly (it has no Light/Dark
    /// ThemeDictionary scoping, so a flat lookup is reliable). Dark reads AccentFillBrushSource.Foreground
    /// (ThemeResource AccentFillColorDefaultBrush), a Windows-adjusted "brighter" shade that reads
    /// better than the raw color in Dark theme.
    /// </summary>
    private Color GetAccentColorForTheme() =>
        RootGrid.ActualTheme == ElementTheme.Dark
            ? ((SolidColorBrush)AccentFillBrushSource.Foreground).Color
            : (Color)Application.Current.Resources["SystemAccentColor"];

    /// <summary>
    /// PowerToggleAlternateButton's icon/label Foreground, per state. Paused reads
    /// AccentBrushSource.Foreground (ThemeResource AccentTextFillColorPrimaryBrush) - Windows' own
    /// accent-as-text shade, tuned to read well against the translucent background. Starting reads
    /// SuccessBrushSource.Foreground (SystemFillColorSuccessBrush), the same green used elsewhere for
    /// StatusTone.Success. Applied for every state shown while Paused, including hover-"Stop" (which
    /// deliberately doesn't get its own distinct color). Hover/press shading (Lighten/Darken) is
    /// layered on top, since the fill itself never shades.
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
    /// Formats a completed-action count as "{count} click"/"{count} clicks" (Click mode) or
    /// "{count} jiggle"/"{count} jiggles" (Jiggle mode), singular only when count == 1.
    /// </summary>
    private static string FormatActionCount(int count, AutomationMode mode)
    {
        var noun = mode == AutomationMode.Click ? "click" : "jiggle";
        return count == 1 ? $"{count} {noun}" : $"{count} {noun}s";
    }

    /// <summary>
    /// Reverts StatusTextBlock back to plain "Off" styling once the user reconfigures automation after
    /// a run has ended, so a lingering "Stopped after N clicks" result doesn't stay attached to
    /// settings that no longer describe it. Guarded to never stomp on a live status while running.
    /// </summary>
    private void ResetStatusToOffIfNotRunning()
    {
        if (_engine.IsRunning)
        {
            return;
        }

        SetStatusText("Off", StatusTone.Muted);
    }

    // How long Jiggle mode's one-shot "Jiggling now" (StatusKind.JiggleStarting) stays on screen before
    // later status reports are allowed to overwrite it - purely cosmetic, independent of
    // MouseAutomationEngine's actual interval timer.
    private static readonly TimeSpan JiggleStartingStatusHoldDuration = TimeSpan.FromMilliseconds(300);
    private bool _statusHoldActive;
    private StatusChangedEventArgs? _pendingStatusAfterHold;

    // How much PowerToggleAlternateButton's current per-state text color is blended toward white/black
    // for its hover/pressed shades (see Lighten/Darken/GetAlternateButtonForeground) - gives it the
    // same tactile feedback the native accent PowerToggleButton gets for free. Tuned by eye.
    private const double PausedButtonHoverLightenAmount = 0.12;
    private const double PausedButtonPressedDarkenAmount = 0.18;

    // GetCountdownFontSize's step size (points knocked off per extra hours digit) and floor. The floor
    // is set to exactly the 100h+ tier's size, so shrinking effectively stops there.
    private const double PowerButtonCountdownFontStepDown = 2;
    private const double PowerButtonMinimumCountdownFontSize = 14;

    // Real alpha (0..1) for GetAlternateButtonBackground, so the fill reads as a faint, translucent
    // tint rather than a bold solid block, for both states.
    private const double PausedButtonBackgroundOpacity = 0.3;

    // StartImminentBlinkIfNeeded's fade range/speed for PowerToggleLabel's opacity during the last 3
    // seconds of a regular per-action countdown (StatusKind.Imminent). Duration is one direction of the
    // AutoReverse fade, so a full cycle takes twice this - tuned for roughly six pulses across the
    // 3-second window. ImminentBlinkMinOpacity of 0.25 keeps the text faintly legible mid-fade.
    private const double ImminentBlinkMinOpacity = 0.25;
    private static readonly TimeSpan ImminentBlinkHalfCycleDuration = TimeSpan.FromSeconds(0.25);

    // StartImminentBlinkIfNeeded suppresses the blink entirely (leaving the countdown text at a
    // constant, unblinking full opacity) when _runningInterval is below this - on a short interval the
    // 3-second Imminent window covers a large fraction of every single cycle, so the blink would be
    // running almost constantly rather than reading as a distinct "about to fire" cue.
    private static readonly TimeSpan ImminentBlinkMinimumInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Which of PowerToggleButton/PowerToggleAlternateButton is currently shown, and - when it's the
    /// latter - which of its two states: pause-on-movement (accent-colored) or Click mode's startup
    /// grace period (green), the latter only while "Stop button display" is set to Countdown.
    /// </summary>
    private enum AlternateButtonState
    {
        Hidden,
        Paused,
        Starting
    }

    // Mirrors which of PowerToggleButton/PowerToggleAlternateButton ShowPowerButton last made the
    // "shown" one - kept as a plain field rather than re-checking .Visibility everywhere.
    private AlternateButtonState _alternateButtonState;

    /// <summary>
    /// Shows exactly one of PowerToggleButton/PowerToggleAlternateButton (Visibility.Visible) and
    /// hides the other - the only place either element's shown/hidden state is ever touched.
    /// </summary>
    private void ShowPowerButton(AlternateButtonState state)
    {
        _alternateButtonState = state;
        PowerToggleButton.Visibility = state == AlternateButtonState.Hidden ? Visibility.Visible : Visibility.Collapsed;
        PowerToggleAlternateButton.Visibility = state == AlternateButtonState.Hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    // Non-null exactly while the Imminent blink is running - lets StartImminentBlinkIfNeeded no-op on
    // every call after the first instead of restarting the animation's phase on every ~100ms status
    // tick, which would turn a smooth pulse into a stutter.
    private Storyboard? _imminentBlinkStoryboard;

    /// <summary>
    /// Starts (once) a repeating fade between full opacity and ImminentBlinkMinOpacity on
    /// PowerToggleLabel, called only while the engine's last report is StatusKind.Imminent (the last 3
    /// seconds of a regular per-action countdown). Only touches Opacity - PowerToggleButton's native
    /// ControlTemplate never animates that property via its own CommonStates Storyboards, so this
    /// can't fight the native Checked-state rendering.
    /// No-ops below ImminentBlinkMinimumInterval - this check lives here rather than at the call site
    /// so it's impossible to start the blink from anywhere without it applying.
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
    /// Stops the Imminent blink (idempotent) and explicitly resets PowerToggleLabel.Opacity back to 1,
    /// since Storyboard.Stop() leaves the animated property wherever the fade happened to be mid-cycle.
    /// Called from every branch that could otherwise leave a stale blink running under text it was
    /// never meant to apply to.
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
        // Guards against a race: the engine's background loop reports the current tick and only then
        // awaits its next Task.Delay, so a Stop() landing on the UI thread during that delay can flip
        // _engine.IsRunning false before that already-queued callback runs. Without this check, the
        // stale callback would stomp the just-set "Off" text with the countdown text it captured.
        if (!_engine.IsRunning)
        {
            return;
        }

        _lastEngineStatusKind = e.Kind;
        _lastEngineStatusText = e.Text;
        _lastEngineStatusRemaining = e.Remaining;

        // StatusKind.Paused (Caution) and StatusKind.Imminent (Critical) only get their attention tones
        // when the status bar text is itself the countdown/pause indicator - that's Counter mode and
        // the toggle-off state, since the button never shows anything but plain "Stop" there.
        // IsCountdownDisplayActive is the exception: its Stop button already carries both cues itself
        // (accent background while paused, live countdown text - see GetAlternateButtonBackground/
        // UpdatePowerButtonCountdownDisplay), and the status bar there just shows the running count, so
        // tinting it too would be a redundant second indicator.
        // StatusKind.Starting is the same story: while IsCountdownDisplayActive, Click mode's startup
        // grace shows its own green countdown on PowerToggleAlternateButton, so the status bar's copy
        // falls back to Muted "Off" instead of duplicating it. JiggleStarting uses the same Muted tone
        // as every ordinary running state - it's not a distinct color, just a distinct one-shot text.
        var tone = e.Kind switch
        {
            StatusKind.Starting when IsCountdownDisplayActive => StatusTone.Muted,
            StatusKind.Starting => StatusTone.Success,
            StatusKind.Imminent when !IsCountdownDisplayActive => StatusTone.Critical,
            StatusKind.Paused when !IsCountdownDisplayActive => StatusTone.Caution,
            _ => StatusTone.Muted
        };
        SetStatusText(GetStatusBarText(e), tone);
        UpdateTaskbarProgress(e);
        _trayIconService.UpdateState(isRunning: true, isPaused: e.Kind == StatusKind.Paused, mode: _runningMode);
        UpdatePowerButtonRunningDisplay();
        UpdateRunningDurationTargetTimeLabel();
        UpdateRunningTimeTargetTimeLabel();
    }

    /// <summary>
    /// Static-target override for AutoStopButtonLabel while a run is in progress under
    /// AutoStopMode.Duration - called from ApplyEngineStatus on every engine status tick (~100ms).
    /// No-ops unless _autoStopMode is currently Duration and _durationStopDeadline was actually set.
    /// Deliberately shows a fixed target time, not a live countdown, via FormatAutoStopDateTime (the
    /// same "Today"/"Tomorrow"/absolute-date formatting AutoStopMode.DateTime uses) against
    /// _durationStopDeadline - the absolute stop time computed once at Start. AutoStopIcon.Glyph is
    /// left untouched, still showing Duration's own Stopwatch glyph.
    /// Called every tick rather than once at Start so the "Today"/"Tomorrow" wording keeps up if a run
    /// is still going when it rolls past midnight - falls out for free since this already runs
    /// continuously. Never mutates _stopDuration or AutoStopDialog's staged fields; reverting to the
    /// static display on stop needs no special handling since every stop path calls
    /// UpdateAutoStopButtonLabel() anyway.
    /// </summary>
    private void UpdateRunningDurationTargetTimeLabel()
    {
        if (_autoStopMode != AutoStopMode.Duration || _durationStopDeadline is not { } deadline)
        {
            return;
        }

        AutoStopButtonLabel.Text = FormatAutoStopDateTime(deadline);
    }

    /// <summary>
    /// Static-target override for AutoStopButtonLabel while a run is in progress under AutoStopMode.Time
    /// - called from ApplyEngineStatus on every engine status tick (~100ms). No-ops unless
    /// _autoStopMode is currently Time and _timeStopDeadline was actually set.
    /// Same "Today"/"Tomorrow" formatting the idle summary uses, but this override stays pinned to the
    /// exact deadline resolved at Start (_timeStopDeadline) rather than recomputing "next occurrence"
    /// fresh, same reasoning as UpdateRunningDurationTargetTimeLabel. Called every tick so the wording
    /// keeps up if the run is still going when it rolls past midnight, for free.
    /// </summary>
    private void UpdateRunningTimeTargetTimeLabel()
    {
        if (_autoStopMode != AutoStopMode.Time || _timeStopDeadline is not { } deadline)
        {
            return;
        }

        AutoStopButtonLabel.Text = FormatAutoStopDateTime(deadline);
    }

    /// <summary>
    /// In Counter display mode, the status bar always shows the engine's own text verbatim. While
    /// IsCountdownDisplayActive, once the first action has fired the status bar shows just the running
    /// click/jiggle count (e.g. "23 jiggles"), including throughout a pause - the resume countdown
    /// itself lives on the button instead (see UpdatePowerButtonCountdownDisplay).
    /// Click-mode's startup grace (StatusKind.Starting) is an earlier exception: since
    /// PowerToggleAlternateButton already shows that countdown, repeating it here would duplicate the
    /// same timer, so this falls back to plain "Off" instead. Every other case keeps showing e.Text -
    /// this checks DisplayedActionCount rather than the raw count so Jiggle mode's one-shot
    /// "Jiggling now" never hits this branch at all (DisplayedActionCount is never 0 once it's been
    /// reported), falling straight through to the count-vs-text split below from its very first instant.
    /// While ShowStopButtonDisplayToggle is off, once the first action has fired the status bar
    /// combines both count and phrase on one line, e.g. "Jiggling in 3m 14s \u00B7 23 jiggles" - the
    /// phrase is e.Text verbatim in every case, including Paused ("Resuming in 12s"/"Paused").
    /// One more exception here: right after the very first click/jiggle, e.Text is exactly the
    /// current mode's own "Clicked"/"Jiggling now" caption and the count is still 1 - showing
    /// "Clicked \u00B7 1 click" reads as redundant when nothing else has happened yet, so this shows the
    /// bare caption instead, same as the DisplayedActionCount==0 branch above already does. Only for
    /// that literal caption text, not "Resuming now" - a pause-triggered resume reads fine combined
    /// with a count even at 1, since it's describing a real gap in the run, not its very start.
    /// </summary>
    private string GetStatusBarText(StatusChangedEventArgs e)
    {
        if (DisplayedActionCount == 0)
        {
            return e.Kind == StatusKind.Starting && IsCountdownDisplayActive
                ? "Off"
                : e.Text;
        }

        if (!_settingsPanel.IsStopButtonDisplayShown)
        {
            var firstActionCaption = _runningMode == AutomationMode.Click ? "Clicked" : "Jiggling now";
            if (DisplayedActionCount == 1 && e.Text == firstActionCaption)
            {
                return e.Text;
            }

            return $"{e.Text} \u00B7 {DisplayedActionCountText}";
        }

        return IsCountdownDisplayActive ? DisplayedActionCountText : e.Text;
    }

    /// <summary>
    /// Mirrors the engine's countdown onto the taskbar icon's progress bar when ShowTaskbarProgressToggle
    /// is on - no-ops otherwise (turning the setting off mid-run needs its own explicit Clear() call, see
    /// ShowTaskbarProgressToggle_Toggled). e.Progress is null for a "Paused" report with no countdown of
    /// its own yet; _lastTaskbarProgress keeps the bar at wherever it already was instead of snapping to 0.
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
    /// ModeSegmentedControl's selection changes, whether from a user tap or SetSelectedMode forcing one
    /// programmatically (used by the tray's "Start Auto Click"/"Start Jiggle" items). Skips everything
    /// below while _isSyncingModeSelection is set, so UpdateModeIndicators' one-way visual sync never
    /// replays these side effects for a change that didn't originate from the user or the tray.
    /// Updates _isJiggleModeSelected (the real source of truth, read via CurrentSelectedMode), plays the
    /// newly selected icon's wiggle/spin animation, resets the status text if not running, refreshes the
    /// auto-stop label, persists LastMode, and keeps the tray tooltip's mode name live. Finishes with
    /// UpdateModeIndicators() so every path that changes _isJiggleModeSelected runs through one refresh
    /// point (its own sync is a no-op here since SelectedIndex is already correct).
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
    /// Sets the selected Auto Click/Jiggle mode to a specific value, as opposed to a direct segment tap.
    /// Shared by the tray's "Start Auto Click"/"Start Jiggle" items, which need to force one specific
    /// mode rather than toggle it. No-ops if the requested mode is already selected. Setting
    /// SelectedIndex fires ModeSegmentedControl_SelectionChanged for real, so a tray-triggered change
    /// runs the same logic as a direct segment tap.
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
    /// needs a manual refresh, since Segmented handles its own selected/unselected/disabled visuals
    /// natively. The SelectedIndex assignment is wrapped in _isSyncingModeSelection so it never replays
    /// ModeSegmentedControl_SelectionChanged's side effects for what's just a one-way visual sync, not
    /// an actual user- or tray-driven mode change.
    /// </summary>
    private void UpdateModeIndicators()
    {
        _isSyncingModeSelection = true;
        ModeSegmentedControl.SelectedIndex = _isJiggleModeSelected ? 1 : 0;
        _isSyncingModeSelection = false;
    }

    /// <summary>
    /// Syncs _isRandomizeIntervalEnabled from RandomizeIntervalToggle.IsOn and refreshes the toggle's
    /// AutomationProperties.Name/ToolTip. Locked while automation is running via SetInputsEnabled, so
    /// this can only fire while idle. Persists to ConfigService.RandomizeInterval, guarded by
    /// _isInitializing so LoadConfigIntoUi's programmatic seeding doesn't write the value straight back.
    /// </summary>
    private void RandomizeIntervalToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _isRandomizeIntervalEnabled = RandomizeIntervalToggle.IsOn;
        UpdateRandomizeIntervalIndicator();

        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.RandomizeInterval = _isRandomizeIntervalEnabled);
    }

    /// <summary>
    /// Refreshes RandomizeIntervalToggle's AutomationProperties.Name/ToolTip so screen readers and
    /// tooltips announce the current on/off state.
    /// </summary>
    private void UpdateRandomizeIntervalIndicator()
    {
        AutomationProperties.SetName(
            RandomizeIntervalToggle,
            _isRandomizeIntervalEnabled ? "Randomize interval, On" : "Randomize interval, Off");
        ToolTipService.SetToolTip(
            RandomizeIntervalToggle,
            _isRandomizeIntervalEnabled ? "Randomize interval: On" : "Randomize interval: Off");

        // RandomizeIntervalIndicatorIcon: the Interval card's header-row "Shuffle" glyph badge - the
        // toggle above lives inside a flyout and isn't visible at rest, so this is the only at-a-glance
        // sign randomize-interval is on.
        RandomizeIntervalIndicatorIcon.Visibility =
            _isRandomizeIntervalEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================================================
    // Interval presets (IntervalOptionsButton/IntervalOptionsFlyout) - variable-length (up to
    // MaxIntervalPresets) quick-select values, editable/addable inline on the Interval card. Flow:
    // arm flyout -> select slot -> edit on card -> accept/discard.
    // ============================================================================================

    /// <summary>
    /// Loads AppConfig.IntervalPresetHours/Minutes/Seconds/Milliseconds into
    /// _intervalPresetHours/Minutes/Seconds/Milliseconds, falling back to AppConfig's own defaults if
    /// config.json doesn't have four same-length arrays of at most MaxIntervalPresets entries each.
    /// Called once from LoadConfigIntoUi, then renders the result onto the flyout's buttons.
    /// </summary>
    private void LoadIntervalPresets(AppConfig config)
    {
        var defaults = new AppConfig();
        var length = config.IntervalPresetHours?.Length ?? -1;
        var isValid = length is >= 0 and <= MaxIntervalPresets
            && config.IntervalPresetMinutes?.Length == length
            && config.IntervalPresetSeconds?.Length == length
            && config.IntervalPresetMilliseconds?.Length == length;

        _intervalPresetHours = new List<int>(isValid ? config.IntervalPresetHours! : defaults.IntervalPresetHours);
        _intervalPresetMinutes = new List<int>(isValid ? config.IntervalPresetMinutes! : defaults.IntervalPresetMinutes);
        _intervalPresetSeconds = new List<int>(isValid ? config.IntervalPresetSeconds! : defaults.IntervalPresetSeconds);
        _intervalPresetMilliseconds = new List<int>(isValid ? config.IntervalPresetMilliseconds! : defaults.IntervalPresetMilliseconds);

        RefreshIntervalPresetFlyoutButtons();
    }

    /// <summary>
    /// Renders _intervalPresetHours/Minutes/Seconds/Milliseconds onto the flyout's up-to-6 apply buttons
    /// (_intervalPresetFlyoutButtons): slots 0..count-1 as real presets, every slot beyond that Collapsed.
    /// IntervalPresetFlyoutRow2 is Collapsed once no real preset reaches into it - leaving an empty row's
    /// Grid Visible would still reserve its spacing in the flyout's StackPanel. IntervalPresetAddTileButton
    /// (in the action row below, next to Edit) is shown separately: visible whenever edit is armed (like
    /// Delete), but disabled while delete is also armed or count &gt;= MaxIntervalPresets, rather than
    /// hidden - so it doesn't jump around as Delete gets toggled.
    ///
    /// IntervalPresetsEditButton/IntervalPresetsDeleteButton have their IsChecked kept in sync with
    /// _isIntervalPresetEditArmed/_isIntervalPresetDeleteArmed here (rather than left to their own toggle
    /// state), since both fields can change elsewhere without the buttons' own Click firing. Edit stays
    /// visible while armed (only IsChecked reflects state); Delete stays Collapsed until Edit is armed.
    /// The title text swaps to a short instruction while armed, further specialized while delete is also
    /// armed. Called from LoadIntervalPresets and every other point that changes the preset count or
    /// either armed state.
    /// </summary>
    private void RefreshIntervalPresetFlyoutButtons()
    {
        var count = _intervalPresetHours.Count;

        for (var i = 0; i < _intervalPresetFlyoutButtons.Length; i++)
        {
            var button = _intervalPresetFlyoutButtons[i];
            if (i < count)
            {
                button.Visibility = Visibility.Visible;
                RenderIntervalPresetFlyoutButton(button, _intervalPresetHours[i], _intervalPresetMinutes[i], _intervalPresetSeconds[i], _intervalPresetMilliseconds[i]);
                AutomationProperties.SetName(button, $"Interval preset {i + 1}");
            }
            else
            {
                button.Visibility = Visibility.Collapsed;
            }

            // "Use preset" ContextFlyout item - disabled while customizing presets.
            _intervalPresetUseMenuItems[i].IsEnabled = !_isIntervalPresetEditArmed;
        }

        IntervalPresetAddTileButton.Visibility = _isIntervalPresetEditArmed ? Visibility.Visible : Visibility.Collapsed;
        IntervalPresetAddTileButton.IsEnabled = !_isIntervalPresetDeleteArmed && count < MaxIntervalPresets;

        var gridVisibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IntervalPresetFlyoutGrid.Visibility = gridVisibility;
        IntervalPresetsFlyoutTitle.Visibility = gridVisibility;
        IntervalPresetsFlyoutTitle.Text = _isIntervalPresetDeleteArmed
            ? "Select a preset to delete"
            : _isIntervalPresetEditArmed
                ? "Select a preset to edit or add"
                : "Presets";

        IntervalPresetFlyoutRow2.Visibility = count > 3 ? Visibility.Visible : Visibility.Collapsed;

        var showEmptyAddButton = count == 0 && !_isIntervalPresetEditArmed;
        IntervalPresetsEditButton.Visibility = showEmptyAddButton ? Visibility.Collapsed : Visibility.Visible;
        IntervalPresetsAddButton.Visibility = showEmptyAddButton ? Visibility.Visible : Visibility.Collapsed;

        IntervalPresetsEditButton.IsChecked = _isIntervalPresetEditArmed;
        IntervalPresetsDeleteButton.Visibility = _isIntervalPresetEditArmed ? Visibility.Visible : Visibility.Collapsed;
        IntervalPresetsDeleteButton.IsChecked = _isIntervalPresetDeleteArmed;

        var hasUndoHistory = _intervalPresetUndoStack.Count > 0;
        IntervalPresetsUndoButton.Visibility = _isIntervalPresetEditArmed || (hasUndoHistory && !_isIntervalPresetUndoDismissed) ? Visibility.Visible : Visibility.Collapsed;
        IntervalPresetsUndoButton.IsEnabled = hasUndoHistory;
        IntervalPresetsRestoreDefaultsButton.Visibility = _isIntervalPresetEditArmed || count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IntervalPresetsRestoreDefaultsButton.IsEnabled = !IntervalPresetsMatchDefaults();

        // Pins Delete/Undo/Restore to the flyout's right edge only while armed - see
        // IntervalPresetActionsSpacerColumn's own XAML comment.
        IntervalPresetActionsSpacerColumn.Width = _isIntervalPresetEditArmed
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(6, GridUnitType.Pixel);
    }

    /// <summary>
    /// IntervalPresetsEditButton's Click handler - a ToggleButton, so this arms/un-arms edit-mode
    /// selection on the flyout's preset buttons (see IntervalPresetFlyoutButton_Click) based on its
    /// post-click IsChecked. Deliberately does not auto-select any slot - unlike AutoStop's preset
    /// editing (which shares a dialog with its buttons), this feature's editing surface is the Interval
    /// card, a separate part of the window, so the user must pick a slot or the Add tile themselves.
    /// Un-arming also un-arms IntervalPresetsDeleteButton, since it's never visible without Edit armed.
    /// Does not touch _intervalPresetUndoStack (its lifecycle is governed only by
    /// IntervalOptionsFlyout_Opening), but does toggle _isIntervalPresetUndoDismissed - see that field's
    /// own comment.
    /// </summary>
    private void IntervalPresetsEditButton_Click(object sender, RoutedEventArgs e)
    {
        _isIntervalPresetEditArmed = IntervalPresetsEditButton.IsChecked == true;
        _isIntervalPresetUndoDismissed = !_isIntervalPresetEditArmed;
        if (!_isIntervalPresetEditArmed)
        {
            _isIntervalPresetDeleteArmed = false;
        }

        RefreshIntervalPresetFlyoutButtons();
    }

    /// <summary>
    /// IntervalPresetsDeleteButton's Click handler - a ToggleButton, only ever visible while
    /// _isIntervalPresetEditArmed is also true (RefreshIntervalPresetFlyoutButtons). Arms/un-arms
    /// _isIntervalPresetDeleteArmed from the button's own post-click IsChecked - see that field's own
    /// comment for what changes while it's true (IntervalPresetFlyoutButton_Click's own delete branch).
    /// </summary>
    private void IntervalPresetsDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _isIntervalPresetDeleteArmed = IntervalPresetsDeleteButton.IsChecked == true;
        RefreshIntervalPresetFlyoutButtons();
    }

    /// <summary>
    /// IntervalOptionsFlyout's Closed handler - safety net for edit mode being armed but light-dismissed
    /// before a slot or the Add tile was picked, so reopening the flyout doesn't show it stuck armed.
    /// Only resets the armed flag: in-progress card edit mode (_editingIntervalPresetIndex/
    /// _isAddingIntervalPreset) is left alone, since closing this flyout is also exactly what
    /// SelectIntervalPresetForEditing/EnterIntervalPresetAddMode do to reveal the card - the guard below
    /// tells the two cases apart. Does not touch _intervalPresetUndoStack; that lifecycle is governed
    /// only by IntervalOptionsFlyout_Opening.
    /// </summary>
    private void IntervalOptionsFlyout_Closed(object sender, object e)
    {
        if (_isIntervalPresetEditArmed && _editingIntervalPresetIndex is null && !_isAddingIntervalPreset)
        {
            _isIntervalPresetEditArmed = false;
            _isIntervalPresetDeleteArmed = false;
            RefreshIntervalPresetFlyoutButtons();
        }
    }

    /// <summary>
    /// IntervalOptionsFlyout's Opening handler - unconditionally clears _intervalPresetUndoStack every
    /// time the flyout is about to show; this is the only place the stack gets cleared, so an action
    /// taken while edit mode isn't armed still stays undo-able for the rest of that session. Also calls
    /// RefreshIntervalPresetFlyoutButtons, since the flyout's content isn't recreated fresh each open, so
    /// IntervalPresetsUndoButton's Visibility would otherwise still reflect the stack's stale state from
    /// last time the flyout closed.
    /// </summary>
    private void IntervalOptionsFlyout_Opening(object sender, object e)
    {
        _intervalPresetUndoStack.Clear();
        _isIntervalPresetUndoDismissed = false;
        RefreshIntervalPresetFlyoutButtons();
    }

    /// <summary>
    /// Dual-purpose Click handler for all 9 numbered preset-grid buttons. While edit mode isn't armed,
    /// applies the clicked slot's value to the live interval and closes the flyout. While armed AND
    /// delete is also armed, deletes the clicked slot instead (checked first, since delete takes
    /// priority). Otherwise, while just edit-armed, selects the slot for inline editing on the card.
    /// Index here is always a real slot (0..count-1); the Add tile has its own separate Click handler.
    /// </summary>
    private void IntervalPresetFlyoutButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var index = Array.IndexOf(_intervalPresetFlyoutButtons, button);

        if (!_isIntervalPresetEditArmed)
        {
            ApplyIntervalPreset(_intervalPresetHours[index], _intervalPresetMinutes[index], _intervalPresetSeconds[index], _intervalPresetMilliseconds[index]);
            IntervalOptionsFlyout.Hide();
            return;
        }

        if (_isIntervalPresetDeleteArmed)
        {
            DeleteIntervalPreset(index);
            return;
        }

        SelectIntervalPresetForEditing(index);
    }

    /// <summary>
    /// Reads the 0-based preset index off a MenuFlyoutItem's Tag (one per numbered preset button's own
    /// ContextFlyout).
    /// </summary>
    private static int GetIntervalPresetMenuItemIndex(object sender) => int.Parse((string)((MenuFlyoutItem)sender).Tag);

    /// <summary>
    /// "Use preset" ContextFlyout item's Click handler - same apply-and-close action as
    /// IntervalPresetFlyoutButton_Click's not-armed branch, reachable directly by right-click. Its
    /// IsEnabled is kept in sync with !_isIntervalPresetEditArmed by RefreshIntervalPresetFlyoutButtons,
    /// so this can only be invoked while not armed anyway.
    /// </summary>
    private void IntervalPresetUseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var index = GetIntervalPresetMenuItemIndex(sender);
        ApplyIntervalPreset(_intervalPresetHours[index], _intervalPresetMinutes[index], _intervalPresetSeconds[index], _intervalPresetMilliseconds[index]);
        IntervalOptionsFlyout.Hide();
    }

    /// <summary>
    /// "Edit" ContextFlyout item's Click handler - a shortcut straight to SelectIntervalPresetForEditing,
    /// past the "click Edit presets first" flow.
    /// </summary>
    private void IntervalPresetEditMenuItem_Click(object sender, RoutedEventArgs e) => SelectIntervalPresetForEditing(GetIntervalPresetMenuItemIndex(sender));

    /// <summary>
    /// "Delete" ContextFlyout item's Click handler - a shortcut straight to DeleteIntervalPreset, past
    /// the "arm Edit then Delete first" flow. Still pushes onto the session Undo stack like a normal
    /// delete.
    /// </summary>
    private void IntervalPresetDeleteMenuItem_Click(object sender, RoutedEventArgs e) => DeleteIntervalPreset(GetIntervalPresetMenuItemIndex(sender));

    /// <summary>
    /// IntervalPresetAddTileButton's Click handler - starts composing a brand-new preset on the
    /// Interval card. Only ever visible while under MaxIntervalPresets, so no cap check is needed here.
    /// </summary>
    private void IntervalPresetAddTileButton_Click(object sender, RoutedEventArgs e)
    {
        EnterIntervalPresetAddMode();
    }

    /// <summary>
    /// IntervalPresetsAddButton's Click handler - only visible in place of IntervalPresetsEditButton at 0
    /// configured presets while unarmed (RefreshIntervalPresetFlyoutButtons). Same landing spot as
    /// IntervalPresetAddTileButton_Click above.
    /// </summary>
    private void IntervalPresetsAddButton_Click(object sender, RoutedEventArgs e)
    {
        EnterIntervalPresetAddMode();
    }

    /// <summary>
    /// Deletes one interval preset outright - IntervalPresetFlyoutButton_Click's landing spot while
    /// delete is armed (also reachable via the right-click "Delete" item regardless of armed state).
    /// Pushes a DeletePresetUndoAction holding the removed preset's values onto _intervalPresetUndoStack
    /// before removing it, clears _isIntervalPresetUndoDismissed, removes <paramref name="index"/> from
    /// all four staged Lists, persists the result, and re-renders. Deliberately does not close the
    /// flyout or un-arm anything, so the user can delete several presets in a row.
    /// </summary>
    private void DeleteIntervalPreset(int index)
    {
        _intervalPresetUndoStack.Add(new DeletePresetUndoAction((_intervalPresetHours[index], _intervalPresetMinutes[index], _intervalPresetSeconds[index], _intervalPresetMilliseconds[index])));
        _isIntervalPresetUndoDismissed = false;

        _intervalPresetHours.RemoveAt(index);
        _intervalPresetMinutes.RemoveAt(index);
        _intervalPresetSeconds.RemoveAt(index);
        _intervalPresetMilliseconds.RemoveAt(index);

        ConfigService.Update(c =>
        {
            c.IntervalPresetHours = _intervalPresetHours.ToArray();
            c.IntervalPresetMinutes = _intervalPresetMinutes.ToArray();
            c.IntervalPresetSeconds = _intervalPresetSeconds.ToArray();
            c.IntervalPresetMilliseconds = _intervalPresetMilliseconds.ToArray();
        });

        RefreshIntervalPresetFlyoutButtons();
    }

    /// <summary>
    /// IntervalPresetsUndoButton's Click handler - pops the most recent entry off
    /// _intervalPresetUndoStack (no-op if empty) and reverses it, LIFO order: a DeletePresetUndoAction
    /// re-appends its preset onto the end of the four live Lists (not its original index); a
    /// RestoreDefaultsUndoAction replaces all four live Lists wholesale with its snapshot. Either branch
    /// then persists the result and re-renders. Stays within MaxIntervalPresets automatically in both
    /// branches, since each undo just restores state that was already within the cap.
    /// </summary>
    private void UndoLastIntervalPresetAction()
    {
        if (_intervalPresetUndoStack.Count == 0)
        {
            return;
        }

        var lastIndex = _intervalPresetUndoStack.Count - 1;
        var action = _intervalPresetUndoStack[lastIndex];
        _intervalPresetUndoStack.RemoveAt(lastIndex);

        if (action is DeletePresetUndoAction deleteAction)
        {
            _intervalPresetHours.Add(deleteAction.Preset.Hours);
            _intervalPresetMinutes.Add(deleteAction.Preset.Minutes);
            _intervalPresetSeconds.Add(deleteAction.Preset.Seconds);
            _intervalPresetMilliseconds.Add(deleteAction.Preset.Milliseconds);
        }
        else if (action is RestoreDefaultsUndoAction restoreAction)
        {
            _intervalPresetHours = restoreAction.PreviousPresets.Select(p => p.Hours).ToList();
            _intervalPresetMinutes = restoreAction.PreviousPresets.Select(p => p.Minutes).ToList();
            _intervalPresetSeconds = restoreAction.PreviousPresets.Select(p => p.Seconds).ToList();
            _intervalPresetMilliseconds = restoreAction.PreviousPresets.Select(p => p.Milliseconds).ToList();
        }

        ConfigService.Update(c =>
        {
            c.IntervalPresetHours = _intervalPresetHours.ToArray();
            c.IntervalPresetMinutes = _intervalPresetMinutes.ToArray();
            c.IntervalPresetSeconds = _intervalPresetSeconds.ToArray();
            c.IntervalPresetMilliseconds = _intervalPresetMilliseconds.ToArray();
        });

        RefreshIntervalPresetFlyoutButtons();
    }

    private void IntervalPresetsUndoButton_Click(object sender, RoutedEventArgs e)
    {
        UndoLastIntervalPresetAction();
    }

    /// <summary>
    /// True when all four live preset Lists already equal a fresh AppConfig()'s defaults, value-for-value
    /// in order. Gates IntervalPresetsRestoreDefaultsButton.IsEnabled, so clicking Restore while already
    /// at the defaults - a no-op that would otherwise still push a pointless undo entry - is prevented
    /// outright, whether that state was reached via Restore or by manually deleting back down to it.
    /// </summary>
    private bool IntervalPresetsMatchDefaults()
    {
        var defaults = new AppConfig();
        return IntervalPresetListEquals(_intervalPresetHours, defaults.IntervalPresetHours)
            && IntervalPresetListEquals(_intervalPresetMinutes, defaults.IntervalPresetMinutes)
            && IntervalPresetListEquals(_intervalPresetSeconds, defaults.IntervalPresetSeconds)
            && IntervalPresetListEquals(_intervalPresetMilliseconds, defaults.IntervalPresetMilliseconds);
    }

    private static bool IntervalPresetListEquals(List<int> current, int[] defaults)
    {
        if (current.Count != defaults.Length)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (current[i] != defaults[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// IntervalPresetsRestoreDefaultsButton's Click handler - resets all four staged Lists back to
    /// AppConfig's hardcoded defaults and persists the result. Before overwriting, snapshots the current
    /// values into a RestoreDefaultsUndoAction pushed onto _intervalPresetUndoStack, so this reset is
    /// itself undoable and chains with any prior pending delete-undo entries rather than wiping them out.
    /// Also clears _isIntervalPresetUndoDismissed, same as DeleteIntervalPreset. Only ever reachable while
    /// IntervalPresetsMatchDefaults() is false, so no no-op guard is needed here.
    /// </summary>
    private void RestoreDefaultIntervalPresets()
    {
        var snapshot = new List<(int Hours, int Minutes, int Seconds, int Milliseconds)>();
        for (var i = 0; i < _intervalPresetHours.Count; i++)
        {
            snapshot.Add((_intervalPresetHours[i], _intervalPresetMinutes[i], _intervalPresetSeconds[i], _intervalPresetMilliseconds[i]));
        }
        _intervalPresetUndoStack.Add(new RestoreDefaultsUndoAction(snapshot));
        _isIntervalPresetUndoDismissed = false;

        var defaults = new AppConfig();
        _intervalPresetHours = new List<int>(defaults.IntervalPresetHours);
        _intervalPresetMinutes = new List<int>(defaults.IntervalPresetMinutes);
        _intervalPresetSeconds = new List<int>(defaults.IntervalPresetSeconds);
        _intervalPresetMilliseconds = new List<int>(defaults.IntervalPresetMilliseconds);

        ConfigService.Update(c =>
        {
            c.IntervalPresetHours = _intervalPresetHours.ToArray();
            c.IntervalPresetMinutes = _intervalPresetMinutes.ToArray();
            c.IntervalPresetSeconds = _intervalPresetSeconds.ToArray();
            c.IntervalPresetMilliseconds = _intervalPresetMilliseconds.ToArray();
        });

        RefreshIntervalPresetFlyoutButtons();
    }

    private void IntervalPresetsRestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreDefaultIntervalPresets();
    }

    /// <summary>
    /// Selects an EXISTING interval preset slot for inline editing directly on the Interval card - the
    /// armed flyout's own click landing spot (IntervalPresetFlyoutButton_Click). Closes the flyout (the
    /// card, not the flyout, is the actual editing surface) and hands off to
    /// EnterIntervalPresetCardEditMode with that slot's own committed value and the "Edit preset"
    /// caption.
    /// </summary>
    private void SelectIntervalPresetForEditing(int index)
    {
        _isIntervalPresetEditArmed = false;
        _editingIntervalPresetIndex = index;
        IntervalOptionsFlyout.Hide();
        EnterIntervalPresetCardEditMode("Edit preset", _intervalPresetHours[index], _intervalPresetMinutes[index], _intervalPresetSeconds[index], _intervalPresetMilliseconds[index]);
    }

    /// <summary>
    /// Starts composing a brand-new interval preset directly on the Interval card - the armed flyout's
    /// own Add-tile click landing spot (IntervalPresetFlyoutButton_Click). Closes the flyout and hands
    /// off to EnterIntervalPresetCardEditMode with an all-zero starting value and the "Add preset"
    /// caption - AcceptIntervalPresetEdit appends whatever the user leaves this at as a brand-new slot,
    /// rather than overwriting an existing one.
    /// </summary>
    private void EnterIntervalPresetAddMode()
    {
        _isIntervalPresetEditArmed = false;
        _isAddingIntervalPreset = true;
        IntervalOptionsFlyout.Hide();
        EnterIntervalPresetCardEditMode("Add preset", hours: 0, minutes: 0, seconds: 0, milliseconds: 0);
    }

    /// <summary>
    /// Shared entry point for both SelectIntervalPresetForEditing and EnterIntervalPresetAddMode - stashes
    /// MinutesBox/SecondsBox's real committed values (_preEditIntervalMinutes/Seconds, restored by
    /// FinishIntervalPresetEditMode once editing ends), then swaps the Interval card into edit-mode look:
    /// caption text changes to captionText, the randomize indicator icon hides, IntervalOptionsButton/
    /// ModeSegmentedControl/AutoStopToggle/AutoStopButton/AutoStopCaption/PowerToggleButton all disable
    /// (nothing should change mode, auto-stop, or start a run out from under an in-progress edit),
    /// Accept/Discard buttons show, StatusTextBlock explains the disabled Start, and MinutesBox/
    /// SecondsBox's SpinButtonPlacementMode flips from Compact to Hidden (restored in
    /// FinishIntervalPresetEditMode). Finally loads the given value into
    /// MinutesBox/SecondsBox via ApplyIntervalPreset - with the caller's _editingIntervalPresetIndex/
    /// _isAddingIntervalPreset already set beforehand, so IntervalBox_ValueChanged's guard skips
    /// persisting this preview write. Focus lands on HoursBox (Advanced) or MinutesBox (Basic).
    /// </summary>
    private void EnterIntervalPresetCardEditMode(string captionText, int hours, int minutes, int seconds, int milliseconds)
    {
        _preEditIntervalMinutes = double.IsNaN(MinutesBox.Value) ? 0 : MinutesBox.Value;
        _preEditIntervalSeconds = double.IsNaN(SecondsBox.Value) ? 0 : SecondsBox.Value;

        IntervalCaptionTextBlock.Text = captionText;
        RandomizeIntervalIndicatorIcon.Visibility = Visibility.Collapsed;
        IntervalOptionsButton.IsEnabled = false;
        ModeSegmentedControl.IsEnabled = false;
        AutoStopToggle.IsEnabled = false;
        AutoStopButton.IsEnabled = false;
        AutoStopCaption.IsEnabled = false;
        PowerToggleButton.IsEnabled = false;
        MinutesBox.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        SecondsBox.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        IntervalPresetAcceptButton.Visibility = Visibility.Visible;
        IntervalPresetDiscardButton.Visibility = Visibility.Visible;

        // Mirrored into TrayIconService so its "Start" menu items visibly gray out while this edit is
        // active, rather than silently rejecting a click (see PowerToggleButton_Checked's own guard).
        _trayIconService.SetIntervalPresetEditActive(true);

        // Explains why the hotkey/tray "Start" items are (silently) doing nothing right now - reverted
        // to the real status by FinishIntervalPresetEditMode via ResetStatusToOffIfNotRunning.
        SetStatusText(EditingIntervalPresetStatusText, StatusTone.Muted, EditingIntervalPresetStatusOpacity);

        ApplyIntervalPreset(hours, minutes, seconds, milliseconds);

        var focusTarget = _isAdvancedIntervalDisplayEnabled ? (Control)HoursBox : MinutesBox;
        focusTarget.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// IntervalPresetAcceptButton's Click handler - see AcceptIntervalPresetEdit's own doc comment.
    /// </summary>
    private void IntervalPresetAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptIntervalPresetEdit();
    }

    /// <summary>
    /// IntervalPresetDiscardButton's Click handler - see AbandonIntervalPresetEditIfActive's own doc
    /// comment.
    /// </summary>
    private void IntervalPresetDiscardButton_Click(object sender, RoutedEventArgs e)
    {
        AbandonIntervalPresetEditIfActive();
    }

    /// <summary>
    /// Commits the interval preset currently being edited/composed on the Interval card. Reads
    /// MinutesBox/SecondsBox via ReadCommittedOrTypedValue rather than raw .Value, since NumberBox only
    /// re-parses typed text into .Value on focus-loss/Enter, and IntervalPresetField_KeyDown's
    /// PreviewKeyDown handling suppresses that Enter-triggered re-parse - without this, an Enter-key
    /// commit could silently save a stale value instead of what's currently typed. Rejects a
    /// combined-zero value with a status message and returns early (leaving edit mode active so the
    /// user can fix it), otherwise decomposes the total back into Hours/Minutes/Seconds/Milliseconds
    /// (DecomposeIntervalTotalMilliseconds), overwrites the edited slot or appends a new one, persists
    /// the four Lists, re-renders the flyout, and hands off to FinishIntervalPresetEditMode.
    /// </summary>
    private void AcceptIntervalPresetEdit()
    {
        var editedMinutes = ReadCommittedOrTypedValue(MinutesBox, fractionDigits: 0);
        var editedSeconds = ReadCommittedOrTypedValue(SecondsBox, fractionDigits: 3);
        if (editedMinutes * 60 + editedSeconds == 0)
        {
            SetStatusText("Enter an interval greater than 0.", StatusTone.Accent);
            return;
        }

        var (hours, minutes, seconds, milliseconds) = DecomposeIntervalTotalMilliseconds(editedMinutes, editedSeconds);

        if (_isAddingIntervalPreset)
        {
            if (_intervalPresetHours.Count < MaxIntervalPresets)
            {
                _intervalPresetHours.Add(hours);
                _intervalPresetMinutes.Add(minutes);
                _intervalPresetSeconds.Add(seconds);
                _intervalPresetMilliseconds.Add(milliseconds);
            }
        }
        else
        {
            var index = _editingIntervalPresetIndex!.Value;
            _intervalPresetHours[index] = hours;
            _intervalPresetMinutes[index] = minutes;
            _intervalPresetSeconds[index] = seconds;
            _intervalPresetMilliseconds[index] = milliseconds;
        }

        ConfigService.Update(c =>
        {
            c.IntervalPresetHours = _intervalPresetHours.ToArray();
            c.IntervalPresetMinutes = _intervalPresetMinutes.ToArray();
            c.IntervalPresetSeconds = _intervalPresetSeconds.ToArray();
            c.IntervalPresetMilliseconds = _intervalPresetMilliseconds.ToArray();
        });

        FinishIntervalPresetEditMode();
    }

    /// <summary>
    /// Abandons whichever interval preset edit is currently active (editing an existing slot, or
    /// composing a new one) without touching _intervalPresetHours/Minutes/Seconds/Milliseconds at all -
    /// a no-op if neither is active, same guard convention AbandonDurationPresetEditIfActive uses, so
    /// this is safe to call unconditionally (e.g. ShowSettingsOverlay). Note PowerToggleButton_Checked
    /// deliberately does NOT call this - it rejects a hotkey/tray start outright instead, leaving the
    /// edit untouched (see its own guard).
    /// </summary>
    private void AbandonIntervalPresetEditIfActive()
    {
        if (_editingIntervalPresetIndex is null && !_isAddingIntervalPreset)
        {
            return;
        }

        FinishIntervalPresetEditMode();
    }

    /// <summary>
    /// Shared cleanup for both AcceptIntervalPresetEdit and AbandonIntervalPresetEditIfActive - clears
    /// the editing/adding/armed flags first (so the MinutesBox/SecondsBox restore below runs through
    /// IntervalBox_ValueChanged's normal, non-preset-editing branch), then restores the card's normal
    /// look (caption, re-enables the controls EnterIntervalPresetCardEditMode disabled - AutoStopButton
    /// only if AutoStopToggle.IsOn - and restores SpinButtonPlacementMode) and the real live interval
    /// value (_preEditIntervalMinutes/Seconds). Also re-renders the flyout's buttons so a stale
    /// armed-mode look never lingers into the next open, and reverts StatusTextBlock's "Editing
    /// preset" message back to "Off".
    /// </summary>
    private void FinishIntervalPresetEditMode()
    {
        _editingIntervalPresetIndex = null;
        _isAddingIntervalPreset = false;
        _isIntervalPresetEditArmed = false;
        _trayIconService.SetIntervalPresetEditActive(false);

        IntervalCaptionTextBlock.Text = "Interval";
        IntervalOptionsButton.IsEnabled = true;
        ModeSegmentedControl.IsEnabled = true;
        AutoStopToggle.IsEnabled = true;
        AutoStopButton.IsEnabled = AutoStopToggle.IsOn;
        AutoStopCaption.IsEnabled = true;
        PowerToggleButton.IsEnabled = true;
        MinutesBox.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact;
        SecondsBox.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact;
        IntervalPresetAcceptButton.Visibility = Visibility.Collapsed;
        IntervalPresetDiscardButton.Visibility = Visibility.Collapsed;

        MinutesBox.Value = _preEditIntervalMinutes;
        SecondsBox.Value = _preEditIntervalSeconds;
        if (_isAdvancedIntervalDisplayEnabled)
        {
            PopulateAdvancedIntervalFieldsFromBasic();
        }

        // Clears the "Editing preset" message set by EnterIntervalPresetCardEditMode. IsRunning is
        // always false here (IntervalOptionsButton is disabled while running, so edit mode can't have
        // been entered), same guard convention as this method's other callers.
        ResetStatusToOffIfNotRunning();

        UpdateRandomizeIntervalIndicator();
        RefreshIntervalPresetFlyoutButtons();
    }

    /// <summary>
    /// PreviewKeyDown handler for the Interval card's MinutesBox/SecondsBox/HoursBox/
    /// AdvancedMinutesBox/AdvancedSecondsBox/MillisecondsBox. Uses tunneling PreviewKeyDown (not
    /// bubbling KeyDown), since NumberBox's inner InputBox commits Enter and marks the routed KeyDown
    /// Handled as part of that commit, so a bubbling handler would never see it. No-op unless interval
    /// preset edit mode is active; Enter accepts (AcceptIntervalPresetEdit), Escape discards
    /// (AbandonIntervalPresetEditIfActive), both marking e.Handled = true so NumberBox's own internal
    /// Enter-commit doesn't also run. No ContentDialog is involved here, so unlike
    /// AutoStopPresetField_KeyDown there's no Escape-dismiss safety net needed.
    /// </summary>
    private void IntervalPresetField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editingIntervalPresetIndex is null && !_isAddingIntervalPreset)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            AcceptIntervalPresetEdit();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            AbandonIntervalPresetEditIfActive();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Writes one Hours/Minutes/Seconds/Milliseconds preset value straight into MinutesBox/SecondsBox
    /// (the source of truth regardless of display mode), via the same all-integer-milliseconds
    /// conversion used for the reverse direction, avoiding floating-point drift. Fires
    /// IntervalBox_ValueChanged same as user typing would, so the value gets clamped/persisted normally.
    /// Only refreshes the Advanced fields when Advanced display is active - while Basic is active they
    /// get refreshed fresh the next time the user switches to Advanced anyway.
    /// </summary>
    private void ApplyIntervalPreset(int hours, int minutes, int seconds, int milliseconds)
    {
        var totalMs = hours * 3_600_000L + minutes * 60_000L + seconds * 1000L + milliseconds;
        MinutesBox.Value = totalMs / 60_000;
        SecondsBox.Value = totalMs % 60_000 / 1000.0;

        if (_isAdvancedIntervalDisplayEnabled)
        {
            PopulateAdvancedIntervalFieldsFromBasic();
        }
    }

    /// <summary>
    /// The exact inverse of ApplyIntervalPreset's Hours/Minutes/Seconds/Milliseconds -&gt; MinutesBox/
    /// SecondsBox composition - used by AcceptIntervalPresetEdit to read back out whatever total the
    /// user left MinutesBox/SecondsBox showing, as a preset's four-component value. Rounds to the
    /// nearest whole millisecond before decomposing, purely to guard against floating-point error in
    /// the seconds * 1000 multiplication (e.g. 30.5 * 1000 landing on 30499.999999999996).
    /// </summary>
    private static (int Hours, int Minutes, int Seconds, int Milliseconds) DecomposeIntervalTotalMilliseconds(double minutesValue, double secondsValue)
    {
        var minutes = double.IsNaN(minutesValue) ? 0 : minutesValue;
        var seconds = double.IsNaN(secondsValue) ? 0 : secondsValue;
        var totalMs = (long)Math.Round(minutes) * 60_000L + (long)Math.Round(seconds * 1000);

        var hours = (int)(totalMs / 3_600_000L);
        var remainder = totalMs % 3_600_000L;
        var mins = (int)(remainder / 60_000L);
        remainder %= 60_000L;
        var secs = (int)(remainder / 1000L);
        var ms = (int)(remainder % 1000L);

        return (hours, mins, secs, ms);
    }

    /// <summary>
    /// Total-hours reading of one interval preset value, used purely to decide the 100h+ cap in
    /// RenderIntervalPresetFlyoutButton.
    /// </summary>
    private static double IntervalPresetTotalHours(int hours, int minutes, int seconds, int milliseconds)
    {
        var totalMs = hours * 3_600_000L + minutes * 60_000L + seconds * 1000L + milliseconds;
        return totalMs / 3_600_000.0;
    }

    /// <summary>
    /// The nonzero components of one interval preset value, formatted ("Xh"/"Ym"/"Zs"/"Wms") and in
    /// Hours-Minutes-Seconds-Milliseconds order, dropping every zero-valued component entirely - the
    /// shared building block RenderIntervalPresetFlyoutButton picks however many of these (up to 2)
    /// it actually displays.
    /// </summary>
    private static List<string> BuildNonZeroIntervalComponents(int hours, int minutes, int seconds, int milliseconds)
    {
        var parts = new List<string>(4);
        if (hours > 0)
        {
            parts.Add($"{hours}h");
        }

        if (minutes > 0)
        {
            parts.Add($"{minutes}m");
        }

        if (seconds > 0)
        {
            parts.Add($"{seconds}s");
        }

        if (milliseconds > 0)
        {
            parts.Add($"{milliseconds}ms");
        }

        return parts;
    }

    /// <summary>
    /// Full, untruncated "Xh Ym Zs Wms" label for one interval preset value - used as the ToolTip text
    /// on a button whose Content was truncated by RenderIntervalPresetFlyoutButton, so the real value
    /// stays discoverable on hover. Falls back to "0ms" for a fully-zero value.
    /// </summary>
    private static string FormatIntervalPresetFull(int hours, int minutes, int seconds, int milliseconds)
    {
        var parts = BuildNonZeroIntervalComponents(hours, minutes, seconds, milliseconds);
        return parts.Count > 0 ? string.Join(" ", parts) : "0ms";
    }

    /// <summary>
    /// Renders one interval preset onto one of the flyout's one-click apply Buttons - Content plus a
    /// ToolTip when a component is actually hidden. If TotalHours &gt; 100, Content collapses to "99h+"
    /// with the full value in the ToolTip (wins over everything below). Otherwise at most 2 of the four
    /// Hours/Minutes/Seconds/Milliseconds components are shown, in priority order: 0 nonzero -> "N/A", no
    /// ToolTip; 1-2 nonzero -> shown as-is, no ToolTip; 3-4 nonzero -> only the first 2 shown with a
    /// trailing "+" on the second, ToolTip carries the untruncated value.
    /// </summary>
    private static void RenderIntervalPresetFlyoutButton(Button button, int hours, int minutes, int seconds, int milliseconds)
    {
        if (IntervalPresetTotalHours(hours, minutes, seconds, milliseconds) > 100)
        {
            button.Content = "99h+";
            ToolTipService.SetToolTip(button, FormatIntervalPresetFull(hours, minutes, seconds, milliseconds));
            return;
        }

        var parts = BuildNonZeroIntervalComponents(hours, minutes, seconds, milliseconds);
        if (parts.Count == 0)
        {
            button.Content = "N/A";
            button.ClearValue(ToolTipService.ToolTipProperty);
        }
        else if (parts.Count <= 2)
        {
            button.Content = string.Join(" ", parts);
            button.ClearValue(ToolTipService.ToolTipProperty);
        }
        else
        {
            button.Content = $"{parts[0]} {parts[1]}+";
            ToolTipService.SetToolTip(button, FormatIntervalPresetFull(hours, minutes, seconds, milliseconds));
        }
    }

    /// <summary>
    /// Applies _settingsPanel.ShowAdvancedIntervalDisplay by syncing _isAdvancedIntervalDisplayEnabled,
    /// swapping BasicIntervalRow/AdvancedIntervalRow's Visibility, and - only when switching to Advanced
    /// - populating the four Hours/Minutes/Seconds/Milliseconds fields from MinutesBox.Value/
    /// SecondsBox.Value (see PopulateAdvancedIntervalFieldsFromBasic). No equivalent population runs
    /// switching back to Basic: those two boxes are kept continuously up to date by
    /// AdvancedIntervalInputBox_TextChanged the entire time Advanced is showing.
    /// Called once at startup from LoadConfigIntoUi and again whenever
    /// SettingsPanel.ShowAdvancedIntervalDisplayChanged fires. Persistence itself happens in
    /// SettingsPanel.IntervalDisplayRadioButton_Checked, so this method doesn't duplicate it.
    /// </summary>
    private void UpdateAdvancedIntervalDisplayMode()
    {
        _isAdvancedIntervalDisplayEnabled = _settingsPanel.ShowAdvancedIntervalDisplay;
        ApplyAdvancedIntervalDisplayVisibility();
        SyncIntervalDisplayToggle();
    }

    /// <summary>
    /// Applies _isAdvancedIntervalDisplayEnabled to BasicIntervalRow/AdvancedIntervalRow's Visibility
    /// and - only when switching TO Advanced - populates the four Hours/Minutes/Seconds/Milliseconds
    /// fields from MinutesBox.Value/SecondsBox.Value. Factored out of UpdateAdvancedIntervalDisplayMode
    /// so IntervalDisplayToggle_Toggled (the flyout's own copy of this setting) can reuse it instead of
    /// duplicating the visibility swap + population logic.
    /// </summary>
    private void ApplyAdvancedIntervalDisplayVisibility()
    {
        BasicIntervalRow.Visibility = _isAdvancedIntervalDisplayEnabled ? Visibility.Collapsed : Visibility.Visible;
        AdvancedIntervalRow.Visibility = _isAdvancedIntervalDisplayEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (_isAdvancedIntervalDisplayEnabled)
        {
            PopulateAdvancedIntervalFieldsFromBasic();

            // Deferred one dispatcher tick: AdvancedIntervalRow's four NumberBoxes haven't gone through
            // their own first real Arrange pass yet at the point Visibility flips above, so each box's
            // glyph layout can be left stuck invisible if refreshed too early (see
            // RefreshStaleNumberBoxTextLayout).
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
    /// Mirrors _isAdvancedIntervalDisplayEnabled onto IntervalDisplayToggle.IsOn, guarded by
    /// _isSyncingIntervalDisplayToggle so the programmatic assignment doesn't replay
    /// IntervalDisplayToggle_Toggled's side effects, then refreshes its AutomationProperties.Name/ToolTip.
    /// </summary>
    private void SyncIntervalDisplayToggle()
    {
        _isSyncingIntervalDisplayToggle = true;
        IntervalDisplayToggle.IsOn = _isAdvancedIntervalDisplayEnabled;
        _isSyncingIntervalDisplayToggle = false;
        UpdateIntervalDisplayToggleIndicator();
    }

    /// <summary>
    /// Refreshes IntervalDisplayToggle's AutomationProperties.Name/ToolTip so screen readers and
    /// tooltips announce the current on/off state - same pattern as UpdateRandomizeIntervalIndicator.
    /// </summary>
    private void UpdateIntervalDisplayToggleIndicator()
    {
        AutomationProperties.SetName(
            IntervalDisplayToggle,
            _isAdvancedIntervalDisplayEnabled ? "Full interval display, On" : "Full interval display, Off");
        ToolTipService.SetToolTip(
            IntervalDisplayToggle,
            _isAdvancedIntervalDisplayEnabled ? "Full interval display: On" : "Full interval display: Off");
    }

    /// <summary>
    /// Handles the Interval card flyout's own IntervalDisplayToggle - the mirror of SettingsPanel's
    /// "Interval display" RadioButtons, kept in sync against the same AppConfig.ShowAdvancedIntervalDisplay
    /// key. No-ops while _isSyncingIntervalDisplayToggle is set (fired from SyncIntervalDisplayToggle's
    /// own assignment, not a real user toggle). Otherwise applies the same visibility swap + field
    /// population as UpdateAdvancedIntervalDisplayMode, pushes the change back into SettingsPanel, and
    /// refreshes this toggle's own indicator.
    /// </summary>
    private void IntervalDisplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncingIntervalDisplayToggle)
        {
            return;
        }

        _isAdvancedIntervalDisplayEnabled = IntervalDisplayToggle.IsOn;
        ApplyAdvancedIntervalDisplayVisibility();
        _settingsPanel.SetShowAdvancedIntervalDisplay(_isAdvancedIntervalDisplayEnabled);
        UpdateIntervalDisplayToggleIndicator();
    }

    /// <summary>
    /// Basic -> Advanced conversion: converts MinutesBox.Value/SecondsBox.Value into whole
    /// Hours/Minutes/Seconds/Milliseconds via an all-integer-milliseconds intermediate, so there's no
    /// floating-point drift (e.g. Minutes=60, Seconds=30 -> 1h 0m 30s 0ms exactly). Guarded by
    /// _isSyncingAdvancedIntervalFields so the ValueChanged/TextChanged handlers these writes fire
    /// don't immediately try to convert back and overwrite MinutesBox/SecondsBox mid-population.
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
    /// blank-coercion hooks (see HookAdvancedIntervalBoxLiveSync), then does the equivalent
    /// MaxLength/clear-button/blank-coercion setup for Basic's MinutesBox/SecondsBox, which don't need
    /// the live-sync part since they're already the canonical source.
    /// </summary>
    private void InitializeAdvancedIntervalLiveSync()
    {
        HookAdvancedIntervalBoxLiveSync(HoursBox, maxLength: 7);
        HookAdvancedIntervalBoxLiveSync(AdvancedMinutesBox, maxLength: 2);
        HookAdvancedIntervalBoxLiveSync(AdvancedSecondsBox, maxLength: 2);
        HookAdvancedIntervalBoxLiveSync(MillisecondsBox, maxLength: 3);

        // Basic's MinutesBox/SecondsBox don't need the live-sync wiring above, but still need their own
        // MaxLength cap - 8 for Minutes (matching its Maximum's digit count) and 6 for Seconds (matching
        // "59.999") - and the same clear-button restyling and blank-to-zero coercion as the Advanced
        // fields (see StyleClearButton/HookBlankCoercion).
        SetInputBoxMaxLength(MinutesBox, maxLength: 8, allowDecimalPoint: false);
        SetInputBoxMaxLength(SecondsBox, maxLength: 6, allowDecimalPoint: true);
        StyleClearButton(MinutesBox);
        StyleClearButton(SecondsBox);
        HookBlankCoercion(MinutesBox);
        HookBlankCoercion(SecondsBox);
    }

    private static void SetInputBoxMaxLength(NumberBox box, int maxLength, bool allowDecimalPoint, bool rejectLeadingZero = false)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => SetInputBoxMaxLength(box, maxLength, allowDecimalPoint, rejectLeadingZero);
            return;
        }

        inputBox.MaxLength = maxLength;

        // allowDecimalPoint doubles as the live-clamp gate here too - the only caller that passes true
        // (SecondsBox) is also the only one of this method's two callers genuinely bounded at 59.
        HookIntervalCharacterFilter(inputBox, allowDecimalPoint, liveClampToFiftyNine: allowDecimalPoint, rejectLeadingZero);
    }

    /// <summary>
    /// Restricts one NumberBox's inner InputBox to digits 0-9 as characters are actually typed - used
    /// for all six interval fields plus AutoStopCountBox - and, only when allowDecimalPoint is true
    /// (SecondsBox alone), a single '.'. NumberBox's own Minimum/Maximum/NumberFormatter only affect
    /// clamping/display at commit time, not what can be typed live, hence this filter.
    /// Uses TextBox.BeforeTextChanging, which fires synchronously before the proposed text is applied
    /// (unlike PreviewKeyDown/KeyDown, which miss paste/IME composition); Cancel = true rejects the
    /// whole pending change so a bad keystroke never partially applies. Empty text always passes
    /// through so Backspace/Delete can still clear the field.
    /// When allowDecimalPoint (SecondsBox), the digit shape is further capped at 2 digits before the
    /// point and 3 after (matching 0-59.999), and ',' is normalized to '.'. Separately, when
    /// liveClampToFiftyNine is set (SecondsBox and AutoStopDuration's Minutes/Seconds), the integer
    /// part is live-clamped to "59" as soon as it would parse above that.
    /// Also sets InputScope="Number" on the inner TextBox directly, since NumberBox itself has no
    /// XAML-settable InputScope in this WindowsAppSDK version.
    /// rejectLeadingZero (AutoStopCountBox alone) rejects a keystroke outright when the proposed text
    /// would be exactly "0", so a lone leading zero is a no-op rather than a visible bare "0" sitting
    /// in the box until commit-time clamping (Minimum="1") catches it.
    /// </summary>
    private static void HookIntervalCharacterFilter(TextBox inputBox, bool allowDecimalPoint, bool liveClampToFiftyNine = false, bool rejectLeadingZero = false)
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

            // SecondsBox alone also accepts ',' as a decimal separator, normalized to a canonical '.'
            // so nothing downstream has to reason about ','. NewText has no public setter, so this
            // can't rewrite it in place: instead validate a normalized copy, Cancel the edit, and queue
            // the corrected Text back onto the dispatcher, restoring the caret to where it would have
            // landed had '.' been typed/pasted directly (works for a multi-character paste too).
            var normalized = allowDecimalPoint && text.Contains(',') ? text.Replace(',', '.') : text;

            if (rejectLeadingZero && normalized == "0")
            {
                args.Cancel = true;
                return;
            }

            // When allowDecimalPoint, cap the digit shape to at most 2 digits before the point and 3
            // after (matching 0-59.999), so e.g. "9999.9" can never even be typed. No restriction
            // applies when allowDecimalPoint is false (MinutesBox, Advanced fields, AutoStopCountBox).
            var sawDecimalPoint = false;
            var digitsBeforePoint = 0;
            var digitsAfterPoint = 0;
            foreach (var ch in normalized)
            {
                if (ch is >= '0' and <= '9')
                {
                    if (allowDecimalPoint)
                    {
                        if (sawDecimalPoint)
                        {
                            if (digitsAfterPoint >= 3)
                            {
                                args.Cancel = true;
                                return;
                            }

                            digitsAfterPoint++;
                        }
                        else
                        {
                            if (digitsBeforePoint >= 2)
                            {
                                args.Cancel = true;
                                return;
                            }

                            digitsBeforePoint++;
                        }
                    }

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

            // Live 59-ceiling clamp on the integer part - gated on liveClampToFiftyNine, independent of
            // allowDecimalPoint (AutoStopDuration's Minutes/Seconds boxes want the ceiling with no
            // decimal point). Runs only after the digit-shape loop above confirmed normalized is
            // well-formed, so integerPart is never more than 2 digits and int.Parse can't overflow.
            if (liveClampToFiftyNine)
            {
                var dotIndex = normalized.IndexOf('.');
                var integerPart = dotIndex >= 0 ? normalized[..dotIndex] : normalized;

                if (integerPart.Length > 0 && int.Parse(integerPart) > 59)
                {
                    var suffix = dotIndex >= 0 ? normalized[dotIndex..] : string.Empty;
                    normalized = "59" + suffix;
                }
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
    /// (Backspace/Delete, or the clear-text "X" button), immediately sets Text to "0" and selects it, so
    /// if the user is mid-replace the next keystroke overwrites the "0" instead of appending after it.
    /// Also live-clears AcceptIntervalPresetEdit's "Enter an interval greater than 0." rejection the
    /// instant the typed (possibly still-uncommitted) Minutes+Seconds stops being zero - mirrors what
    /// writing into MinutesBox/SecondsBox.Value already does for IntervalBox_ValueChanged while Advanced
    /// is showing (see AdvancedIntervalInputBox_TextChanged), since Basic's own typing here never
    /// commits (fires ValueChanged) until blur/Enter.
    /// Used for MinutesBox/SecondsBox; the four Advanced fields get the equivalent blank-coercion check
    /// inline in AdvancedIntervalInputBox_TextChanged since they already have a live TextChanged handler.
    /// </summary>
    private void HookBlankCoercion(NumberBox box)
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
                // Setting box.Value here would NOT re-render Text while still focused - NumberBox only
                // reconciles Text from Value at LostFocus/Enter. Setting Text directly shows "0" right
                // away; the box's own commit-time parse reads it back into Value normally on blur.
                inputBox.Text = "0";
                inputBox.SelectAll();
            }

            if (_editingIntervalPresetIndex is not null || _isAddingIntervalPreset)
            {
                var typedMinutes = ReadCommittedOrTypedValue(MinutesBox, fractionDigits: 0);
                var typedSeconds = ReadCommittedOrTypedValue(SecondsBox, fractionDigits: 3);
                if (typedMinutes * 60 + typedSeconds != 0)
                {
                    SetStatusText(EditingIntervalPresetStatusText, StatusTone.Muted, EditingIntervalPresetStatusOpacity);
                }
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

    /// <summary>
    /// NumberBox-level wrapper around SuppressClearButton(TextBox) below, same relationship
    /// StyleClearButton(NumberBox) has to StyleClearButton(TextBox). Used wherever the built-in clear
    /// "X" button is confirmed inert (SpinButtonPlacementMode="Hidden" fields), so it's suppressed
    /// instead of styled.
    /// </summary>
    private static void SuppressClearButton(NumberBox box)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => SuppressClearButton(box);
            return;
        }

        SuppressClearButton(inputBox);
    }

    private void HookAdvancedIntervalBoxLiveSync(NumberBox box, int maxLength)
    {
        // Runs in the constructor while AdvancedIntervalRow may still be Visibility="Collapsed", and a
        // Collapsed subtree is skipped during layout, so box's ControlTemplate wouldn't exist yet.
        // ApplyTemplate() forces it to materialize synchronously, independent of layout/visibility -
        // a Loaded-based fallback alone would fire too early (before the template applies) and never
        // find the InputBox for the rest of the session.
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => HookAdvancedIntervalBoxLiveSync(box, maxLength);
            return;
        }

        inputBox.MaxLength = maxLength;
        inputBox.TextChanged += AdvancedIntervalInputBox_TextChanged;

        // None of the four Advanced fields support fractional values (each truncated to a whole number),
        // so allowDecimalPoint is always false here, unlike SecondsBox.
        HookIntervalCharacterFilter(inputBox, allowDecimalPoint: false);

        // Suppressed, not styled-and-shown, since the built-in clear "X" is confirmed inert on these
        // fields (unlike Basic's MinutesBox/SecondsBox, where StyleClearButton's version works fine).
        SuppressClearButton(inputBox);

        // box's own FontSize is deliberately 12 so its Header caption stays legible against its longest
        // sibling ("Milliseconds"), but FontSize is inherited, so the inner InputBox's digits would
        // shrink too without this override. 14 matches BasicIntervalRow's MinutesBox/SecondsBox.
        inputBox.FontSize = 14;
    }

    /// <summary>
    /// AutoStopDialog's Duration-mode twin of HookAdvancedIntervalBoxLiveSync: same MaxLength, live
    /// digits-only filtering, and clear-button suppression for AutoStopDurationHoursBox/MinutesBox/
    /// SecondsBox. Restores inputBox.FontSize to 14 for the same inherited-FontSize-from-Header reason
    /// as HookAdvancedIntervalBoxLiveSync.
    /// Deliberately not just a call to HookAdvancedIntervalBoxLiveSync: that method wires TextChanged to
    /// AdvancedIntervalInputBox_TextChanged, which converts this field's value together with its
    /// siblings into a total and writes it back into the unrelated MinutesBox/SecondsBox - reusing that
    /// here would corrupt the app's actual interval. These three fields are independent, so this hooks
    /// a minimal duration-specific TextChanged handler instead: live blank-to-"0" coercion only, no
    /// cross-field write-back.
    /// Same ApplyTemplate()-now/Loaded-fallback structure as HookAdvancedIntervalBoxLiveSync, since
    /// AutoStopDurationContainer starts Visibility="Collapsed".
    /// </summary>
    private void HookAutoStopDurationBoxLiveSync(NumberBox box, int maxLength, bool liveClampToFiftyNine = false)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => HookAutoStopDurationBoxLiveSync(box, maxLength, liveClampToFiftyNine);
            return;
        }

        inputBox.MaxLength = maxLength;
        inputBox.TextChanged += (_, _) =>
        {
            if (inputBox.Text.Length == 0)
            {
                inputBox.Text = "0";
                inputBox.SelectAll();
            }
        };

        // None of the three duration fields support fractional values, so allowDecimalPoint is always
        // false here. liveClampToFiftyNine is independent - true for Minutes/Seconds (Maximum="59"),
        // false for Hours.
        HookIntervalCharacterFilter(inputBox, allowDecimalPoint: false, liveClampToFiftyNine);

        SuppressClearButton(inputBox);

        // 14 matches WinUI's default ControlContentThemeFontSize, countering the inherited smaller
        // Header FontSize (see HookAdvancedIntervalBoxLiveSync).
        inputBox.FontSize = 14;
    }

    /// <summary>
    /// Live-syncs AutoStopCountBox's staged preset value while Action count preset editing is active -
    /// hooks the inner InputBox's TextChanged (fires every keystroke) instead of relying on
    /// AutoStopCountBox_ValueChanged (commit-only). Without this, an Enter-key save
    /// (AutoStopPresetField_KeyDown) would persist the value from before the user's final,
    /// uncommitted keystrokes. Reuses ReadLiveAdvancedFieldValue's live-text-read-and-clamp logic;
    /// no-op outside edit mode.
    /// Also writes the selected slot's preset button Content on every keystroke (via
    /// FormatCountPresetValue, showing "N/A" instead of "0") as transient feedback while typing/blank.
    /// A transient 0 here is harmless since AutoStopCountBox_ValueChanged's commit-time revert resolves
    /// a blank field before it can reach Save.
    /// </summary>
    private void HookCountPresetLiveSync()
    {
        var box = AutoStopCountBox;
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => HookCountPresetLiveSync();
            return;
        }

        inputBox.TextChanged += (_, _) =>
        {
            if (_editingCountPresetIndex is not int index)
            {
                return;
            }

            var value = (int)ReadLiveAdvancedFieldValue(box);
            _stagedCountPresetValues[index] = value;
            _countPresetButtons[index].Content = FormatCountPresetValue(value);
        };
    }

    /// <summary>
    /// Duration counterpart of HookCountPresetLiveSync - same rationale. Also live-updates the selected
    /// preset's button label on every keystroke, routed through RefreshDurationPresetButtons (rather
    /// than a raw FormatAutoStopDuration call) so the 100h+ "99h+" cap and the row-wide
    /// hide-seconds-with-"+" rule still apply live. Hooked once per field (three separate calls);
    /// <paramref name="stagedArray"/> is whichever of _stagedDurationPresetHours/Minutes/Seconds
    /// <paramref name="box"/> feeds.
    /// </summary>
    private void HookDurationPresetLiveSync(NumberBox box, int[] stagedArray)
    {
        box.ApplyTemplate();

        if (FindInputBox(box) is not { } inputBox)
        {
            box.Loaded += (_, _) => HookDurationPresetLiveSync(box, stagedArray);
            return;
        }

        inputBox.TextChanged += (_, _) =>
        {
            if (_editingDurationPresetIndex is not int index)
            {
                return;
            }

            stagedArray[index] = (int)ReadLiveAdvancedFieldValue(box);
            RefreshDurationPresetButtons(_stagedDurationPresetHours, _stagedDurationPresetMinutes, _stagedDurationPresetSeconds, isEditing: true);
        };
    }

    /// <summary>
    /// Takes over driving the visibility of the built-in "clear text" (X) button that WinUI's TextBox
    /// ControlTemplate shows on focus + non-empty text, instead of trusting the template's own
    /// "ButtonVisible"/"ButtonCollapsed" VisualStateManager states, which don't fire reliably for every
    /// NumberBox configuration in this app. Used for Basic's MinutesBox/SecondsBox; the four Advanced
    /// fields use SuppressClearButton instead.
    /// Clicking the button clears Text same as always; HookBlankCoercion turns that blank state into
    /// "0" instead of leaving the field empty.
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

        // Deliberately LosingFocus, not LostFocus: clicking DeleteButton moves focus to it first (before
        // its own Click fires on release), so reacting to plain LostFocus would collapse the button and
        // cancel its Click before it fires. LosingFocus exposes where focus is headed
        // (NewFocusedElement), so it can tell "leaving to DeleteButton, about to be clicked" apart from
        // "leaving elsewhere - really hide it".
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
    /// Permanently hides the built-in "clear text" (X) button on fields with
    /// SpinButtonPlacementMode="Hidden" (the four Advanced interval fields, AutoStopCountBox, the
    /// AutoStop duration fields) - not because it can't be made visible, but because clicking/invoking
    /// it there is confirmed inert (tested via SendInput and UIA InvokePattern directly): it never
    /// clears text or changes value. The identical button on Basic's MinutesBox/SecondsBox
    /// (SpinButtonPlacementMode="Compact") works correctly, so hidden spin buttons are suspected to
    /// suppress hit-testing/event-routing to it.
    /// Since a visible-but-inert button is worse than no button, Visibility is forced to Collapsed and
    /// a RegisterPropertyChangedCallback snaps it back to Collapsed every time the template's own
    /// "ButtonVisible" VisualState re-animates it to Visible. The never-blank rule (HookBlankCoercion)
    /// still applies via Backspace/Delete coercion regardless of this button's absence.
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
    /// Hours/Minutes/Seconds/Milliseconds fields while Advanced display is showing - not just on
    /// switch-back or commit. This keeps MinutesBox.Value/SecondsBox.Value correct at all times, which
    /// matters because PowerToggleButton_Checked's Start-time computation reads those two boxes
    /// unconditionally - starting via hotkey while Advanced is showing never blurs any field, so a
    /// commit-only conversion would use a stale interval. Writing into MinutesBox.Value/SecondsBox.Value
    /// fires their existing ValueChanged handler (IntervalBox_ValueChanged), which persists the interval;
    /// this method doesn't duplicate that.
    /// Also coerces this field's blank text to "0" the instant it goes empty, since the clear "X"
    /// button is suppressed on these four fields. Sets Text directly (not sender.Value), since setting
    /// Value while the InputBox still has focus doesn't re-render Text until LostFocus/Enter.
    /// Each field is read independently via ReadLiveAdvancedFieldValue, which truncates and clamps to
    /// its own Minimum/Maximum, but is not written back into its own Value/Text here (that snap-back is
    /// AdvancedIntervalBox_ValueChanged's job at commit). SecondsBox.Value takes the fractional
    /// remainder after whole minutes are removed - why SecondsBox.Maximum is 59.999, not 59.95, or up to
    /// 49ms of precision would be silently clamped away on write-back.
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
    /// Reads one NumberBox's live, uncommitted inner-InputBox text (not .Value, which lags until
    /// commit), truncates it to a whole number, and clamps it to that box's own Minimum/Maximum.
    /// Empty/non-numeric/negative text all fall back to/clamp to 0 rather than throwing.
    /// Despite the name, nothing here is Advanced-interval-specific - HookCountPresetLiveSync/
    /// HookDurationPresetLiveSync reuse it verbatim, for the same reason: NumberBox.ValueChanged only
    /// fires on commit, never on every keystroke. InvariantCulture, not CurrentCulture, keeps every
    /// parse of these fields' text consistent (see ReadCommittedOrTypedValue).
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
    /// Commit-time (tier 1) clamp+truncation for the four Advanced interval NumberBoxes - fires on
    /// commit (blur/Enter/programmatic set), unlike AdvancedIntervalInputBox_TextChanged which fires
    /// live but never touches these boxes' own Value/Text. Clamps to Minimum/Maximum and truncates
    /// (floors) to a whole number, writing back only if it differs. NumberBox does NOT auto-clamp typed
    /// text on its own - Minimum/Maximum only constrain the (hidden) spin buttons, not committed text.
    /// Setting sender.Value from inside this handler doesn't cause a second ValueChanged (no
    /// reentrancy). Skips entirely while _isSyncingAdvancedIntervalFields is set, since Basic ->
    /// Advanced population already writes pre-clamped values.
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
    /// Commit-time (tier 1) clamp+truncation for MinutesBox/SecondsBox, plus config persistence.
    /// MinutesBox truncates (floors) to a whole number; SecondsBox truncates beyond 3 decimal places
    /// (see TruncateToFractionDigits). Also clamps to Minimum/Maximum first, since NumberBox does NOT
    /// auto-clamp typed text on its own - only the spin buttons/arrow keys respect those bounds.
    /// Setting sender.Value from inside this handler doesn't raise a second ValueChanged (no
    /// reentrancy), but this method has exactly one pass per commit, so it must compute minutes/seconds
    /// AFTER the clamp+truncation write, in the same pass, or it would persist the pre-correction value.
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

        // While staging an interval preset under edit (existing slot or new one), the clamp/truncation
        // above still applies, but this write must never reach ConfigService.IntervalMinutes/Seconds or
        // ResetStatusToOffIfNotRunning's "clear stale status" side effect - those are for the real, live
        // interval only. Still live-clears AcceptIntervalPresetEdit's "Enter an interval greater than 0."
        // rejection the instant the staged value becomes non-zero again, mirroring
        // AutoStopDurationBox_ValueChanged's equivalent live-clear for Duration presets.
        if (_editingIntervalPresetIndex is not null || _isAddingIntervalPreset)
        {
            var stagedMinutes = double.IsNaN(MinutesBox.Value) ? 0 : MinutesBox.Value;
            var stagedSeconds = double.IsNaN(SecondsBox.Value) ? 0 : SecondsBox.Value;
            if (stagedMinutes * 60 + stagedSeconds != 0)
            {
                SetStatusText(EditingIntervalPresetStatusText, StatusTone.Muted, EditingIntervalPresetStatusOpacity);
            }

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

    private void AutoStopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SetStopControlsEnabled(AutoStopToggle.IsOn);
        ResetStatusToOffIfNotRunning();
    }

    /// <summary>
    /// Opens the centered, modal Auto Stop configuration dialog. Seeds every mode's staged working
    /// copies (and the controls that edit them) unconditionally, all at once, from whatever is
    /// currently committed. StopTimePicker's value is always seeded from _timeStopTime, the single
    /// remembered stop time-of-day shared by both AutoStopPickDateToggle states. StopDatePicker's value
    /// defaults to the next actual occurrence of that time (via ResolveNextOccurrenceDate), unless a
    /// still-future _stopDateTime exists from earlier this session, which takes precedence.
    /// AutoStopSegmentedControl.SelectedIndex is set from _lastConfiguredAutoStopMode, not
    /// _autoStopMode, so the dialog still offers last session's choice before re-confirmation.
    /// AutoStopPickDateToggle.IsOn is seeded separately since AutoStopModeToSegmentIndex's DateTime/Time
    /// share-index-0 collapse can't recover it alone. Both assignments are followed by an explicit
    /// UpdateAutoStopModeContainerVisibility() call, since neither control raises its change event when
    /// the assigned value already matches what it had last time.
    /// Only an explicit OK commits the staged values and persists them; Cancel/Escape/any other
    /// dismissal leaves everything untouched.
    /// </summary>
    private async void AutoStopButton_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _stagedStopTime = _timeStopTime;
        _stagedStopDate = _stopDateTime.HasValue && _stopDateTime.Value > now
            ? _stopDateTime.Value.Date
            : ResolveNextOccurrenceDate(_stagedStopTime);
        _stagedAutoStopCount = _autoStopCount;
        // TotalHours, not TimeSpan.Hours - Hours would only be the 0-23 remainder once the duration is
        // 24h+, since TimeSpan rolls anything past 24 into Days instead. Minutes/Seconds don't have
        // this problem.
        _stagedStopDurationHours = (int)_stopDuration.TotalHours;
        _stagedStopDurationMinutes = _stopDuration.Minutes;
        _stagedStopDurationSeconds = _stopDuration.Seconds;

        StopDatePicker.Date = new DateTimeOffset(_stagedStopDate);
        StopTimePicker.Time = _stagedStopTime;
        AutoStopCountBox.Value = _stagedAutoStopCount;
        AutoStopDurationHoursBox.Value = _stagedStopDurationHours;
        AutoStopDurationMinutesBox.Value = _stagedStopDurationMinutes;
        AutoStopDurationSecondsBox.Value = _stagedStopDurationSeconds;

        // _isSeedingAutoStopDialog keeps this initial seed from animating even if SelectionChanged
        // fires (only happens when the new index differs from the control's leftover value) - the
        // dialog's first render must land directly on the right tab, no slide.
        _isSeedingAutoStopDialog = true;
        _autoStopSeedTargetSegmentIndex = AutoStopModeToSegmentIndex(_lastConfiguredAutoStopMode);
        AutoStopSegmentedControl.SelectedIndex = _autoStopSeedTargetSegmentIndex;
        AutoStopPickDateToggle.IsOn = _lastConfiguredAutoStopMode == AutoStopMode.DateTime;
        UpdateAutoStopModeContainerVisibility();
        _autoStopVisibleSegmentIndex = _autoStopSeedTargetSegmentIndex;

        // The very first time this dialog is ever shown, toolkit:Segmented hasn't generated its item
        // containers yet - that happens asynchronously after this point, and as a side effect
        // self-heals its own SelectedIndex, racing (and clobbering) the seed above. Keep
        // _isSeedingAutoStopDialog held until AutoStopSegmentedControl_Loaded reconciles that race
        // instead of clearing it here. Later opens don't re-run container generation, so no race exists
        // and it's safe to clear immediately.
        if (_hasAutoStopSegmentedControlStabilized)
        {
            _isSeedingAutoStopDialog = false;
        }

        // "Action count" tab caption/AutomationProperties.Name and the count NumberBox's own caption +
        // AutomationProperties.Name reflect whichever mode (Click/Jiggle) is selected on the main window
        // - set once here, not live-synced, since ModeSegmentedControl can't be touched while this modal
        // dialog is open.
        var countNoun = CurrentSelectedMode == AutomationMode.Jiggle ? "Jiggles" : "Clicks";
        AutoStopCountSegmentedItemText.Text = countNoun;
        AutomationProperties.SetName(AutoStopCountSegmentedItem, countNoun);
        var countNounLower = countNoun.ToLowerInvariant();
        AutoStopCountCaptionText.Text = countNounLower;
        AutomationProperties.SetName(AutoStopCountBox, countNounLower);

        AutoStopDialog.XamlRoot = Content.XamlRoot;

        // NOT calling RefreshStaleNumberBoxTextLayout() here, before ShowAsync: the dialog isn't
        // composed/shown yet, so AutoStopCountBox's InputBox hasn't gone through its first real Arrange
        // pass (see AutoStopDialog.Opened's subscription in the constructor).
        ContentDialogResult result;
        _isAutoStopDialogOpen = true;
        _trayIconService.SetAutoStopDialogOpen(true);
        try
        {
            result = await AutoStopDialog.ShowAsync();
        }
        finally
        {
            _isAutoStopDialogOpen = false;
            _trayIconService.SetAutoStopDialogOpen(false);
        }

        if (result == ContentDialogResult.Primary)
        {
            _autoStopMode = SegmentIndexToAutoStopMode(AutoStopSegmentedControl.SelectedIndex, AutoStopPickDateToggle.IsOn);
            _lastConfiguredAutoStopMode = _autoStopMode;
            _autoStopCount = _stagedAutoStopCount;
            _stopDateTime = _stagedStopDate.Add(_stagedStopTime);
            // Only overwrites _stopDuration when the staged trio isn't all-zero. This commit block runs
            // for any confirmed mode and always persists every mode's staged values unconditionally -
            // without this guard, leaving the Duration fields at 0h 0m 0s and confirming a different
            // mode would silently clobber the last real Duration setting with a zero one.
            if (_stagedStopDurationHours != 0 || _stagedStopDurationMinutes != 0 || _stagedStopDurationSeconds != 0)
            {
                _stopDuration = new TimeSpan(_stagedStopDurationHours, _stagedStopDurationMinutes, _stagedStopDurationSeconds);
            }
            // StopTimePicker is the single shared time control for both AutoStopPickDateToggle states,
            // so _timeStopTime is always persisted from the same staged value regardless of which state
            // ends up selected.
            _timeStopTime = _stagedStopTime;

            UpdateAutoStopButtonLabel();
            ResetStatusToOffIfNotRunning();

            ConfigService.Update(c =>
            {
                c.AutoStopMode = _autoStopMode.ToString();
                c.AutoStopCount = _autoStopCount;
                // TotalHours, not Hours - see _stagedStopDurationHours' own assignment above for why.
                c.AutoStopDurationHours = (int)_stopDuration.TotalHours;
                c.AutoStopDurationMinutes = _stopDuration.Minutes;
                c.AutoStopDurationSeconds = _stopDuration.Seconds;
                c.AutoStopTimeHour = _timeStopTime.Hours;
                c.AutoStopTimeMinute = _timeStopTime.Minutes;
            });
        }
    }

    /// <summary>
    /// Maps AutoStopMode to/from AutoStopSegmentedControl's fixed 3-item order (0: Date & time, 1:
    /// Duration, 2: Action count) - used by AutoStopButton_Click to preselect the control from
    /// _lastConfiguredAutoStopMode, and to read back whichever item the user landed on at OK time.
    /// AutoStopMode.DateTime and AutoStopMode.Time both map to index 0 - AutoStopPickDateToggle inside
    /// AutoStopDateTimeContainer is what disambiguates them, so unlike the reverse direction
    /// (SegmentIndexToAutoStopMode, which needs the toggle's state to tell them apart) this direction
    /// alone can't and doesn't need to. AutoStopMode.None (which means "never configured") falls back to
    /// index 0 (Date & time, toggle Off - i.e. Time) - the control's own first item, same as this dialog
    /// has always defaulted to whichever item is visually first.
    /// </summary>
    private static int AutoStopModeToSegmentIndex(AutoStopMode mode) => mode switch
    {
        AutoStopMode.Duration => 1,
        AutoStopMode.Count => 2,
        _ => 0
    };

    /// <summary>
    /// Inverse of AutoStopModeToSegmentIndex - reads back the AutoStopMode from
    /// AutoStopSegmentedControl's current SelectedIndex at OK time. Index 0 alone is ambiguous between
    /// DateTime and Time, since they share one tab; isPickDateOn (AutoStopPickDateToggle.IsOn)
    /// disambiguates: On means DateTime (one-shot date+time), Off means Time (recurring time-only stop).
    /// </summary>
    private static AutoStopMode SegmentIndexToAutoStopMode(int index, bool isPickDateOn) => index switch
    {
        0 => isPickDateOn ? AutoStopMode.DateTime : AutoStopMode.Time,
        1 => AutoStopMode.Duration,
        _ => AutoStopMode.Count
    };

    /// <summary>
    /// The next actual occurrence of a given time-of-day: today at that time if it hasn't happened yet,
    /// otherwise tomorrow - same resolution PowerToggleButton_Checked's AutoStopMode.Time branch uses
    /// for the real stopAt. Used here purely for seeding/previewing StopDatePicker while
    /// AutoStopPickDateToggle is Off - a display concern with no effect on the committed _timeStopTime.
    /// </summary>
    private static DateTime ResolveNextOccurrenceDate(TimeSpan timeOfDay)
    {
        var now = DateTime.Now;
        var todayAtTime = now.Date + timeOfDay;
        return todayAtTime > now ? todayAtTime.Date : todayAtTime.Date.AddDays(1);
    }

    /// <summary>
    /// Single place that reconciles AutoStopSegmentedControl's SelectedIndex - and, for its first item,
    /// AutoStopPickDateToggle's IsOn - into which of the three field containers is visible, plus
    /// StopDatePicker.IsEnabled and AutoStopDateTimeDescription's text. Called from
    /// AutoStopSegmentedControl_SelectionChanged/AutoStopPickDateToggle_Toggled, and directly from
    /// AutoStopButton_Click after seeding both, since neither control raises its change event when the
    /// assigned value already matches. Only the container matching SelectedIndex is ever Visible; the
    /// other two are Collapsed - unless animatingContainerSwap is true, in which case
    /// AutoStopSegmentedControl_SelectionChanged's own AnimatePanelTransition call owns the
    /// outgoing/incoming Visibility flips instead (both need to stay Visible together mid-slide), so
    /// this skips them. When Duration becomes visible, also defers a stale-glyph-layout refresh of its
    /// three NumberBoxes to the next dispatcher tick, since this can fire while the dialog is already
    /// open and composed.
    /// </summary>
    private void UpdateAutoStopModeContainerVisibility(bool animatingContainerSwap = false)
    {
        // No explicit AbandonCountPresetEditIfActive/AbandonDurationPresetEditIfActive call needed here:
        // entering edit mode moves focus onto the Count/Hours box, so reaching AutoStopSegmentedControl
        // to change modes necessarily moves focus out of that container first - which
        // AutoStopCountContainer_LosingFocus/AutoStopDurationContainer_LosingFocus already catches and
        // abandons before this method's own call would run.
        var index = AutoStopSegmentedControl.SelectedIndex;
        var isPickDateOn = AutoStopPickDateToggle.IsOn;

        if (!animatingContainerSwap)
        {
            AutoStopDateTimeContainer.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            AutoStopDurationContainer.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            AutoStopCountContainer.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Disabled (grayed out), not hidden, while AutoStopPickDateToggle is Off. Set unconditionally
        // regardless of index - harmless while AutoStopDateTimeContainer is Collapsed, and keeps this
        // one method the single source of truth for the toggle's effect.
        StopDatePicker.IsEnabled = isPickDateOn;

        // While Off, StopDatePicker is a disabled preview of the recurring stop's next occurrence, not
        // a user-editable date - re-resolved here so switching Off always snaps back to that accurate
        // preview, discarding any future date picked while On. Deliberately NOT re-resolved while On,
        // since there StopDatePicker is the user's own authoritative date choice.
        if (!isPickDateOn)
        {
            _stagedStopDate = ResolveNextOccurrenceDate(_stagedStopTime);
            StopDatePicker.Date = new DateTimeOffset(_stagedStopDate);
        }

        // Toggle-driven, not container-driven, since AutoStopMode.Time/DateTime now share one tab.
        AutoStopDateTimeDescription.Text = isPickDateOn
            ? "Stops automation at the selected date & time."
            : "Stops automation at the selected time.";

        if (index == 1)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshStaleNumberBoxTextLayout(AutoStopDurationHoursBox);
                RefreshStaleNumberBoxTextLayout(AutoStopDurationMinutesBox);
                RefreshStaleNumberBoxTextLayout(AutoStopDurationSecondsBox);
            });
        }

        // Any mode/toggle switch clears a previously shown AutoStopDurationZeroWarning/
        // AutoStopDateTimePastWarning - neither should survive into a fresh view of the dialog, only
        // ever appear as the direct result of an OK click (see AutoStopDialog_PrimaryButtonClick) -
        // restoring each warning's own caption to Visible alongside it.
        AutoStopDurationDescription.Visibility = Visibility.Visible;
        AutoStopDurationZeroWarning.Visibility = Visibility.Collapsed;
        AutoStopDateTimeDescription.Visibility = Visibility.Visible;
        AutoStopDateTimePastWarning.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// AutoStopDateTimeContainer/AutoStopDurationContainer/AutoStopCountContainer index-aligned with
    /// AutoStopModeToSegmentIndex/SegmentIndexToAutoStopMode and UpdateAutoStopModeContainerVisibility's
    /// own mapping - used by AutoStopSegmentedControl_SelectionChanged to resolve the animated switch's
    /// outgoing/incoming pair.
    /// </summary>
    private FrameworkElement GetAutoStopTabContainer(int index) => index switch
    {
        0 => AutoStopDateTimeContainer,
        1 => AutoStopDurationContainer,
        _ => AutoStopCountContainer
    };

    /// <summary>
    /// Slides the previously-selected tab container out and the newly-selected one in via
    /// AnimatePanelTransition, mirroring ShowSettingsOverlay/SettingsBackButton_Click's use of the same
    /// helper. Three guarded early-outs, in order:
    ///  - _isSeedingAutoStopDialog: AutoStopButton_Click is seeding the dialog's first render - lands
    ///    directly on the right tab, no slide.
    ///  - _isAutoStopTabTransitioning: a click landed mid-slide. AnimatePanelTransition has no
    ///    re-entrancy guard of its own, so a second call here would race the first - instead, snap
    ///    AutoStopSegmentedControl's selection back to whichever tab the in-flight animation is already
    ///    headed toward (_autoStopVisibleSegmentIndex), ignoring the click. That reassignment
    ///    re-enters this handler synchronously, guarded by _isRevertingAutoStopTabSelection.
    ///  - newIndex == previousIndex: SelectedIndex didn't actually move (e.g. AutoStopPickDateToggle
    ///    round-tripping focus) - just reconcile, no animation.
    /// reverse mirrors AnimatePanelTransition's own convention: true slides incoming in from the left,
    /// used when the newly selected tab sits to the left of the previous one.
    /// </summary>
    private void AutoStopSegmentedControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRevertingAutoStopTabSelection)
        {
            return;
        }

        var newIndex = AutoStopSegmentedControl.SelectedIndex;

        if (_isSeedingAutoStopDialog)
        {
            UpdateAutoStopModeContainerVisibility();
            _autoStopVisibleSegmentIndex = newIndex;
            return;
        }

        if (_isAutoStopTabTransitioning)
        {
            _isRevertingAutoStopTabSelection = true;
            AutoStopSegmentedControl.SelectedIndex = _autoStopVisibleSegmentIndex;
            _isRevertingAutoStopTabSelection = false;
            return;
        }

        var previousIndex = _autoStopVisibleSegmentIndex;
        _autoStopVisibleSegmentIndex = newIndex;

        if (newIndex == previousIndex)
        {
            UpdateAutoStopModeContainerVisibility();
            return;
        }

        _isAutoStopTabTransitioning = true;

        // Reconciles everything except the container Visibility flips themselves (IsEnabled,
        // description text, warning clearing, NumberBox layout refresh) - AnimatePanelTransition below
        // owns the outgoing/incoming Visibility instead.
        UpdateAutoStopModeContainerVisibility(animatingContainerSwap: true);

        AnimatePanelTransition(
            outgoing: GetAutoStopTabContainer(previousIndex),
            incoming: GetAutoStopTabContainer(newIndex),
            reverse: newIndex < previousIndex,
            onCompleted: () => _isAutoStopTabTransitioning = false);
    }

    /// <summary>
    /// Fires the first time AutoStopSegmentedControl is ever attached to the visual tree (every dialog
    /// open after that reuses its already-generated containers, so this is a no-op then). That first
    /// container generation happens asynchronously and self-heals the control's own SelectedIndex
    /// (bouncing through -1, settling on its last item) after AutoStopButton_Click's synchronous seed -
    /// each bounce fires SelectionChanged while _isSeedingAutoStopDialog is still held true, so none of
    /// it animates. Queuing to the next dispatcher tick lets that churn fully drain before reapplying
    /// the real seeded index and releasing _isSeedingAutoStopDialog.
    /// </summary>
    private void AutoStopSegmentedControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasAutoStopSegmentedControlStabilized)
        {
            return;
        }

        _hasAutoStopSegmentedControlStabilized = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            AutoStopSegmentedControl.SelectedIndex = _autoStopSeedTargetSegmentIndex;
            _autoStopVisibleSegmentIndex = _autoStopSeedTargetSegmentIndex;
            UpdateAutoStopModeContainerVisibility();
            _isSeedingAutoStopDialog = false;
        });
    }

    /// <summary>
    /// Toggling "Pick date" mid-dialog needs the same reconciliation
    /// AutoStopSegmentedControl_SelectionChanged triggers, so this just forwards into
    /// UpdateAutoStopModeContainerVisibility rather than duplicating it.
    /// </summary>
    private void AutoStopPickDateToggle_Toggled(object sender, RoutedEventArgs e) => UpdateAutoStopModeContainerVisibility();

    /// <summary>
    /// Treats a commit as "blank" whenever it's NaN or exactly 0, covering whichever NumberBox actually
    /// produces for empty text. No clamping for a genuinely non-blank commit: a literal "0" can't be
    /// typed (see HookIntervalCharacterFilter's rejectLeadingZero), and Minimum="1"/MaxLength=8 already
    /// bound scroll-wheel/arrow-key input natively, so any non-blank NewValue reaching here is already
    /// known-valid.
    /// A blank commit always reverts to a valid fallback: outside edit mode, _stagedAutoStopCount; while
    /// editing a preset, _countPresetValues[editingIndex] (the slot's last saved value, not the staged
    /// one being discarded). Writing sender.Value re-enters this same handler with the fallback as
    /// NewValue, which falls through to the non-blank branch below.
    /// </summary>
    private void AutoStopCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue) || args.NewValue == 0)
        {
            sender.Value = _editingCountPresetIndex is int editingIndex
                ? _countPresetValues[editingIndex]
                : _stagedAutoStopCount;
            return;
        }

        // Edit mode: this box is previewing/editing one specific preset slot, not the real configured
        // count - route the commit into that slot's staged value and its button's live label instead of
        // _stagedAutoStopCount, so an in-progress edit never leaks into the actual config.
        if (_editingCountPresetIndex is int index)
        {
            _stagedCountPresetValues[index] = (int)args.NewValue;
            _countPresetButtons[index].Content = FormatCountPresetValue((int)args.NewValue);
            return;
        }

        _stagedAutoStopCount = (int)args.NewValue;
    }

    /// <summary>
    /// Shared by every place a Count preset button's label gets rendered - shows "N/A" instead of a
    /// literal "0", mirroring RenderDurationPresetButton's all-zero treatment. A committed preset can
    /// never actually be 0, but the live per-keystroke path (HookCountPresetLiveSync) reads raw text and
    /// returns a genuine 0 for blank text, so a preset can transiently show 0 while being typed.
    /// </summary>
    private static string FormatCountPresetValue(int value) => value == 0 ? "N/A" : value.ToString();

    /// <summary>
    /// Falls back to <paramref name="fallback"/> whenever <paramref name="source"/> isn't exactly 3
    /// entries - guards LoadConfigIntoUi against a hand-edited or corrupted config.json, since every
    /// preset row assumes exactly 3 index-aligned slots.
    /// </summary>
    private static int[] LoadPresetArray(int[]? source, int[] fallback) => source is { Length: 3 } ? source : fallback;

    /// <summary>
    /// Renders _countPresetValues onto AutoStopCountContainer's three preset ToggleButtons' Content -
    /// called from LoadConfigIntoUi and from FinishCountPresetEditMode (restoring each label after an
    /// edit ends, whether saved or abandoned).
    /// </summary>
    private void RefreshCountPresetLabels()
    {
        for (var i = 0; i < _countPresetButtons.Length; i++)
        {
            _countPresetButtons[i].Content = FormatCountPresetValue(_countPresetValues[i]);
        }
    }

    /// <summary>
    /// Duration counterpart of RefreshCountPresetLabels - same call sites, delegating to
    /// RefreshDurationPresetButtons against the committed _durationPresetHours/Minutes/Seconds trio
    /// (its other two call sites use the staged arrays instead).
    /// </summary>
    private void RefreshDurationPresetLabels()
    {
        RefreshDurationPresetButtons(_durationPresetHours, _durationPresetMinutes, _durationPresetSeconds, isEditing: false);
    }

    /// <summary>
    /// Renders all three of AutoStopDurationContainer's preset ToggleButtons from a given
    /// Hours/Minutes/Seconds trio of 3-element arrays - the single shared entry point every call site
    /// goes through (RefreshDurationPresetLabels against committed values; the reset/edit-mode call
    /// sites against staged values), centralized because the "hide seconds, mark minutes with a +" rule
    /// below needs to look at all three presets' current values together, not just whichever one a call
    /// site would otherwise touch in isolation.
    /// The row-wide rule (hideSecondsWithPlus): when all three presets simultaneously have Hours AND
    /// Seconds each nonzero, every preset in the row hides its seconds and appends "+" to its minutes
    /// instead (e.g. "1h 30m+" rather than "1h 30m 15s"). Minutes is not part of the gate. Only applies
    /// while not editing - see RenderDurationPresetButton for the full priority order.
    /// </summary>
    private void RefreshDurationPresetButtons(int[] hours, int[] minutes, int[] seconds, bool isEditing)
    {
        var hideSecondsWithPlus = true;
        for (var i = 0; i < hours.Length; i++)
        {
            if (hours[i] <= 0 || seconds[i] <= 0)
            {
                hideSecondsWithPlus = false;
                break;
            }
        }

        for (var i = 0; i < _durationPresetButtons.Length; i++)
        {
            var duration = new TimeSpan(hours[i], minutes[i], seconds[i]);
            RenderDurationPresetButton(_durationPresetButtons[i], duration, hideSecondsWithPlus, isEditing);
        }
    }

    /// <summary>
    /// Renders one Duration preset slot onto its button - Content plus a ToolTip when needed - given the
    /// row-wide hideSecondsWithPlus condition RefreshDurationPresetButtons already computed, and whether
    /// this row is being edited (isEditing). Priority order:
    /// 1. TotalHours &gt; 100: collapses to "99h+" always, regardless of isEditing/hideSecondsWithPlus, so
    ///    an extreme preset can't blow out the row's width.
    /// 2. Otherwise while isEditing: at most 2 of Hours/Minutes/Seconds ever show at once, a zero
    ///    component is never printed. All-three-zero -&gt; "N/A". All-three-nonzero -&gt; "{h}h {m}m" (no
    ///    "+" marker in edit mode, but ToolTip carries the full text). Otherwise shows exactly whichever
    ///    components are nonzero, space-joined.
    /// 3. Otherwise (not editing), once hideSecondsWithPlus is true: "{h}h {m}m+" when Minutes &gt; 0,
    ///    otherwise "{h}h {s}s".
    /// Otherwise defers to FormatAutoStopDuration's useShortSingleUnitForm shorthand.
    /// ToolTip: set (with the real full text) only where a case above hides a nonzero value (the 100h+
    /// cap, rule 2's all-three-nonzero case, rule 3's Minutes&gt;0 case); every other case ClearValue's
    /// it, since a preset can move between cases as it's edited.
    /// </summary>
    private static void RenderDurationPresetButton(ToggleButton button, TimeSpan duration, bool hideSecondsWithPlus, bool isEditing)
    {
        if (duration.TotalHours > 100)
        {
            button.Content = "99h+";
            ToolTipService.SetToolTip(button, FormatAutoStopDuration(duration, useShortSingleUnitForm: true));
        }
        else if (isEditing)
        {
            var hours = (int)duration.TotalHours;
            var hasHours = hours > 0;
            var hasMinutes = duration.Minutes > 0;
            var hasSeconds = duration.Seconds > 0;

            if (!hasHours && !hasMinutes && !hasSeconds)
            {
                button.Content = "N/A";
                button.ClearValue(ToolTipService.ToolTipProperty);
            }
            else if (hasHours && hasMinutes && hasSeconds)
            {
                // Only isEditing case with all three nonzero - collapse to 2 (Hours/Minutes), hide
                // Seconds. No "+" marker in edit mode, but the value stays discoverable via ToolTip.
                button.Content = $"{hours}h {duration.Minutes}m";
                ToolTipService.SetToolTip(button, FormatAutoStopDuration(duration, useShortSingleUnitForm: true));
            }
            else
            {
                // At most 2 of 3 components are nonzero here, so nothing needs hiding - show whichever
                // are actually nonzero, dropping the rest.
                var hoursPart = hasHours ? $"{hours}h" : null;
                var minutesPart = hasMinutes ? $"{duration.Minutes}m" : null;
                var secondsPart = hasSeconds ? $"{duration.Seconds}s" : null;
                button.Content = string.Join(" ", new[] { hoursPart, minutesPart, secondsPart }.Where(p => p is not null));
                button.ClearValue(ToolTipService.ToolTipProperty);
            }
        }
        else if (hideSecondsWithPlus && duration.Minutes > 0)
        {
            button.Content = $"{(int)duration.TotalHours}h {duration.Minutes}m+";
            ToolTipService.SetToolTip(button, FormatAutoStopDuration(duration, useShortSingleUnitForm: true));
        }
        else if (hideSecondsWithPlus)
        {
            button.Content = $"{(int)duration.TotalHours}h {duration.Seconds}s";
            button.ClearValue(ToolTipService.ToolTipProperty);
        }
        else
        {
            button.Content = FormatAutoStopDuration(duration, useShortSingleUnitForm: true);
            button.ClearValue(ToolTipService.ToolTipProperty);
        }
    }

    /// <summary>
    /// Shared Click handler for AutoStopCountContainer's three preset ToggleButtons - a ToggleButton at
    /// all times (even outside edit mode) so they can radio-select among each other once edit mode is
    /// active without swapping control types. Branches on _editingCountPresetIndex:
    /// - Null: applies this button's own preset value (_countPresetValues[index]) to
    ///   AutoStopCountBox.Value, then immediately self-unchecks - a preset shouldn't visually stay
    ///   "pressed" since it's a one-shot action, not a persistent selection.
    /// - Non-null: radio-selects this slot for editing via SelectCountPresetForEditing instead.
    /// </summary>
    private void AutoStopCountPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        var index = Array.IndexOf(_countPresetButtons, button);

        if (_editingCountPresetIndex is null)
        {
            AutoStopCountBox.Value = _countPresetValues[index];
            button.IsChecked = false;
            return;
        }

        ReconcileEditingCountPresetValue();
        SelectCountPresetForEditing(index);
    }

    /// <summary>
    /// Reconciles _stagedCountPresetValues[_editingCountPresetIndex] against AutoStopCountBox's live,
    /// possibly-uncommitted text, applying the same "blank/zero reverts to last saved value" rule
    /// AutoStopCountBox_ValueChanged applies on commit - a backstop for callers (SaveCountPresetEdits,
    /// switching presets mid-edit) that can't assume the box's blur-triggered commit has already run
    /// (confirmed a click straight onto AutoStopCountConfirmEditButton doesn't reliably fire it first).
    /// Reads the inner InputBox text directly via ReadLiveAdvancedFieldValue, kept in sync on every
    /// keystroke regardless of commit. Also re-renders the outgoing slot's button label. No-op if edit
    /// mode isn't active.
    /// </summary>
    private void ReconcileEditingCountPresetValue()
    {
        if (_editingCountPresetIndex is not int index)
        {
            return;
        }

        var liveValue = (int)ReadLiveAdvancedFieldValue(AutoStopCountBox);
        _stagedCountPresetValues[index] = liveValue == 0 ? _countPresetValues[index] : liveValue;
        _countPresetButtons[index].Content = FormatCountPresetValue(_stagedCountPresetValues[index]);
    }

    /// <summary>
    /// Radio-selects one of AutoStopCountContainer's three preset slots for editing - unchecks every
    /// other preset ToggleButton (so exactly one is ever checked while editing), stages AutoStopCountBox
    /// to that slot's staged value, and records the selection (_editingCountPresetIndex) so
    /// AutoStopCountBox_ValueChanged's edit-mode branch knows which slot further typing feeds into. Also
    /// used by EnterCountPresetEditMode to auto-select slot 0 when edit mode turns on.
    /// Also moves keyboard focus onto AutoStopCountBox after staging its value. Without this, clicking
    /// Edit/a preset then pressing Escape has focus somewhere AutoStopPresetField_KeyDown never sees
    /// (only wired to AutoStopCountBox), so Escape falls through to the dialog's own Escape-dismiss and
    /// closes the whole dialog instead of cancelling the row's edit. AutoStopCountContainer_LosingFocus
    /// is this fix's other half, guaranteeing edit mode can't be left active with focus elsewhere.
    /// </summary>
    private void SelectCountPresetForEditing(int index)
    {
        for (var i = 0; i < _countPresetButtons.Length; i++)
        {
            _countPresetButtons[i].IsChecked = i == index;
        }

        _editingCountPresetIndex = index;
        AutoStopCountBox.Value = _stagedCountPresetValues[index];
        AutoStopCountBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// AutoStopCountEditButton's Click handler - a separate plain Button whose only job is starting
    /// editing. AutoStopCountConfirmEditButton_Click (below) is the other half, and only ever saves.
    /// </summary>
    private void AutoStopCountEditButton_Click(object sender, RoutedEventArgs e)
    {
        EnterCountPresetEditMode();
    }

    /// <summary>
    /// AutoStopCountConfirmEditButton's Click handler - Edit and Confirm are separate controls/handlers
    /// rather than one ToggleButton's two Click-time branches.
    /// </summary>
    private void AutoStopCountConfirmEditButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCountPresetEdits();
    }

    /// <summary>
    /// Turns on Action count preset editing - stashes AutoStopCountBox's real value
    /// (_preEditAutoStopCount, restored by FinishCountPresetEditMode), seeds a fresh working copy of
    /// _stagedCountPresetValues from the committed _countPresetValues so an untouched slot's save is a
    /// no-op, swaps which edit/confirm/cancel/reset buttons are visible, and auto-selects slot 0.
    /// </summary>
    private void EnterCountPresetEditMode()
    {
        _preEditAutoStopCount = _stagedAutoStopCount;
        Array.Copy(_countPresetValues, _stagedCountPresetValues, _stagedCountPresetValues.Length);
        AutoStopCountDescription.Text = "Edit presets";
        AutoStopCountDescription.FontWeight = FontWeights.SemiBold;
        SetPresetEditButtonsVisibility(AutoStopCountEditButton, AutoStopCountConfirmEditButton, AutoStopCountCancelEditButton, AutoStopCountResetPresetsButton, isEditing: true);
        SetPresetGridStretch(AutoStopCountPresetGrid, stretch: true);
        SelectCountPresetForEditing(0);
        UpdateAutoStopDialogDefaultButtonState();
    }

    /// <summary>
    /// Persists the in-progress Action count preset edit - copies the working _stagedCountPresetValues
    /// into the committed _countPresetValues and writes them to AppConfig (duplicate values across
    /// slots, or values past the original defaults, are both allowed), then exits edit mode via
    /// FinishCountPresetEditMode. Also the Enter-key save path (AutoStopPresetField_KeyDown calls it
    /// directly).
    /// Calls ReconcileEditingCountPresetValue first, since a staged slot's blank/zero commit depends on
    /// the box's own LostFocus-driven commit having already fired, which isn't guaranteed before this
    /// method's caller runs.
    /// Passes _countPresetValues[_editingCountPresetIndex.Value] - the just-saved value - for
    /// FinishCountPresetEditMode to restore into AutoStopCountBox, so a successful save leaves the field
    /// showing what was just saved rather than reverting to the pre-edit value (that's
    /// AbandonCountPresetEditIfActive's job instead).
    /// </summary>
    private void SaveCountPresetEdits()
    {
        ReconcileEditingCountPresetValue();
        Array.Copy(_stagedCountPresetValues, _countPresetValues, _countPresetValues.Length);
        ConfigService.Update(c => c.AutoStopCountPresets = (int[])_countPresetValues.Clone());
        FinishCountPresetEditMode(_countPresetValues[_editingCountPresetIndex!.Value]);
    }

    /// <summary>
    /// Common cleanup for both ways Action count preset editing can end (SaveCountPresetEdits/
    /// AbandonCountPresetEditIfActive) - swaps the edit/confirm/cancel/reset buttons back to
    /// non-editing, unchecks every preset ToggleButton, clears _editingCountPresetIndex, re-renders
    /// every preset label from _countPresetValues, and restores AutoStopCountBox to
    /// <paramref name="restoreValue"/> (the pre-edit value from AbandonCountPresetEditIfActive, or the
    /// just-saved slot value from SaveCountPresetEdits).
    /// Also writes <paramref name="restoreValue"/> straight into _stagedAutoStopCount rather than
    /// relying solely on the AutoStopCountBox.Value assignment's ValueChanged: a NumberBox doesn't
    /// re-raise ValueChanged when set to the value it already holds, which right after a save would
    /// silently leave _stagedAutoStopCount stale.
    /// </summary>
    private void FinishCountPresetEditMode(int restoreValue)
    {
        AutoStopCountDescription.Text = "Stops automation once this count is reached.";
        AutoStopCountDescription.FontWeight = FontWeights.Normal;
        SetPresetEditButtonsVisibility(AutoStopCountEditButton, AutoStopCountConfirmEditButton, AutoStopCountCancelEditButton, AutoStopCountResetPresetsButton, isEditing: false);
        SetPresetGridStretch(AutoStopCountPresetGrid, stretch: false);
        foreach (var preset in _countPresetButtons)
        {
            preset.IsChecked = false;
        }

        _editingCountPresetIndex = null;
        RefreshCountPresetLabels();
        AutoStopCountBox.Value = restoreValue;
        _stagedAutoStopCount = restoreValue;
        UpdateAutoStopDialogDefaultButtonState();
    }

    /// <summary>
    /// Toggles a preset row's Grid between its default natural-width layout (first three
    /// ColumnDefinitions all Width="Auto") and a full-width layout (those same three set to 1*, so the
    /// presets stretch to fill the row edge-to-edge) - used only while that row is mid-edit; see
    /// EnterCountPresetEditMode/FinishCountPresetEditMode and their Duration counterparts. Only touches
    /// columns 0-2 by fixed index - column 3 holds the Edit button, which stays Auto-width always.
    /// </summary>
    private static void SetPresetGridStretch(Grid grid, bool stretch)
    {
        var width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions[i].Width = width;
        }
    }

    /// <summary>
    /// Silently discards an in-progress Action count preset edit without persisting it - called from
    /// AutoStopDialog_Closing (dialog closing any way - OK, Cancel, or light-dismiss) and
    /// AutoStopCountContainer_LosingFocus (focus leaving the row's controls entirely, including
    /// switching AutoStopSegmentedControl away from Action count). No-op if edit mode isn't active.
    /// </summary>
    private void AbandonCountPresetEditIfActive()
    {
        if (_editingCountPresetIndex is null)
        {
            return;
        }

        FinishCountPresetEditMode(_preEditAutoStopCount);
    }

    /// <summary>
    /// Click handler for AutoStopCountCancelEditButton - visible only while Action count preset editing
    /// is active. Reuses AbandonCountPresetEditIfActive verbatim.
    /// </summary>
    private void AutoStopCountCancelEditButton_Click(object sender, RoutedEventArgs e)
    {
        AbandonCountPresetEditIfActive();
    }

    /// <summary>
    /// Click handler for AutoStopCountResetPresetsButton - resets all three staged preset slots back to
    /// a fresh AppConfig()'s defaults (not a duplicated literal, so there's one place those defaults are
    /// defined). Unlike Save/Cancel, does NOT exit edit mode - a one-shot bulk edit within the session;
    /// the user still needs Confirm to persist it. Updates all three labels immediately (unlike ordinary
    /// typing, which leaves labels showing the old value until Confirm), and re-seeds/refocuses the
    /// shared field with whichever slot is still selected.
    /// </summary>
    private void AutoStopCountResetPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        Array.Copy(new AppConfig().AutoStopCountPresets, _stagedCountPresetValues, _stagedCountPresetValues.Length);
        for (var i = 0; i < _countPresetButtons.Length; i++)
        {
            _countPresetButtons[i].Content = FormatCountPresetValue(_stagedCountPresetValues[i]);
        }

        AutoStopCountBox.Value = _stagedCountPresetValues[_editingCountPresetIndex!.Value];
        AutoStopCountBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Duration counterpart of AutoStopCountPresetButton_Click - same rationale. Reads each preset's
    /// stored trio directly from _durationPresetHours/Minutes/Seconds[index] (not a Content re-parse,
    /// since Content can be any FormatAutoStopDuration output) and writes all three duration fields at
    /// once, fully replacing whatever they previously held.
    /// </summary>
    private void AutoStopDurationPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        var index = Array.IndexOf(_durationPresetButtons, button);

        if (_editingDurationPresetIndex is null)
        {
            AutoStopDurationHoursBox.Value = _durationPresetHours[index];
            AutoStopDurationMinutesBox.Value = _durationPresetMinutes[index];
            AutoStopDurationSecondsBox.Value = _durationPresetSeconds[index];
            button.IsChecked = false;
            return;
        }

        SelectDurationPresetForEditing(index);
    }

    /// <summary>
    /// Duration counterpart of SelectCountPresetForEditing, across all three duration fields at once.
    /// Focus goes to AutoStopDurationHoursBox specifically - the first/leftmost of the trio, matching
    /// Tab order.
    /// </summary>
    private void SelectDurationPresetForEditing(int index)
    {
        for (var i = 0; i < _durationPresetButtons.Length; i++)
        {
            _durationPresetButtons[i].IsChecked = i == index;
        }

        _editingDurationPresetIndex = index;
        AutoStopDurationHoursBox.Value = _stagedDurationPresetHours[index];
        AutoStopDurationMinutesBox.Value = _stagedDurationPresetMinutes[index];
        AutoStopDurationSecondsBox.Value = _stagedDurationPresetSeconds[index];
        AutoStopDurationHoursBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// AutoStopDurationEditButton's Click handler - same rationale as AutoStopCountEditButton_Click.
    /// </summary>
    private void AutoStopDurationEditButton_Click(object sender, RoutedEventArgs e)
    {
        EnterDurationPresetEditMode();
    }

    /// <summary>
    /// AutoStopDurationConfirmEditButton's Click handler - same rationale as
    /// AutoStopCountConfirmEditButton_Click.
    /// </summary>
    private void AutoStopDurationConfirmEditButton_Click(object sender, RoutedEventArgs e)
    {
        SaveDurationPresetEdits();
    }

    /// <summary>
    /// Duration counterpart of EnterCountPresetEditMode, across all three duration fields/staged arrays
    /// at once. Also explicitly restores AutoStopDurationDescription to Visible and Collapses
    /// AutoStopDurationZeroWarning - the real configured Duration value's own zero-check, otherwise only
    /// cleared by editing the real fields, switching modes, or an OK click. Without this, a leftover
    /// warning from an earlier rejected OK click could still be showing when entering preset edit mode,
    /// producing two warning messages on screen at once.
    /// </summary>
    private void EnterDurationPresetEditMode()
    {
        _preEditDurationHours = _stagedStopDurationHours;
        _preEditDurationMinutes = _stagedStopDurationMinutes;
        _preEditDurationSeconds = _stagedStopDurationSeconds;
        Array.Copy(_durationPresetHours, _stagedDurationPresetHours, _stagedDurationPresetHours.Length);
        Array.Copy(_durationPresetMinutes, _stagedDurationPresetMinutes, _stagedDurationPresetMinutes.Length);
        Array.Copy(_durationPresetSeconds, _stagedDurationPresetSeconds, _stagedDurationPresetSeconds.Length);
        AutoStopDurationZeroWarning.Visibility = Visibility.Collapsed;
        AutoStopDurationDescription.Visibility = Visibility.Visible;
        AutoStopDurationDescription.Text = "Edit presets";
        AutoStopDurationDescription.FontWeight = FontWeights.SemiBold;
        SetPresetEditButtonsVisibility(AutoStopDurationEditButton, AutoStopDurationConfirmEditButton, AutoStopDurationCancelEditButton, AutoStopDurationResetPresetsButton, isEditing: true);
        SetPresetGridStretch(AutoStopDurationPresetGrid, stretch: true);
        SelectDurationPresetForEditing(0);
        // Explicit refresh under isEditing:true - SelectDurationPresetForEditing's Value assignments
        // only re-render the buttons as a side effect of ValueChanged, which WinUI skips when the
        // assigned value already matches. Without this, all three buttons could stay stale until the
        // user's first actual edit.
        RefreshDurationPresetButtons(_stagedDurationPresetHours, _stagedDurationPresetMinutes, _stagedDurationPresetSeconds, isEditing: true);
        UpdateAutoStopDialogDefaultButtonState();
    }

    /// <summary>
    /// Duration counterpart of SaveCountPresetEdits. Unlike Count (no validation, since Minimum="1"
    /// already makes an empty value unreachable), Duration has one validation rule: no staged preset
    /// slot can be saved as a fully zero 0h 0m 0s duration - a check separate from the real configured
    /// Duration's own zero-check in AutoStopDialog_PrimaryButtonClick (AutoStopDurationZeroWarning vs.
    /// AutoStopDurationPresetZeroWarning, two distinct TextBlocks).
    /// Checked against all three staged slots, not just the one being edited, since all three get
    /// Array.Copy'd into the real config regardless of which slot the user is looking at - otherwise a
    /// different slot left at 0/0/0 could slip through uncontested. If only the selected slot is the
    /// culprit: "Enter a duration greater than 0." If any other slot is also all-zero: "Please fix the
    /// invalid presets." Either way, rejecting keeps edit mode active until every invalid slot is fixed.
    /// Duplicate values across slots, or values past the original defaults, are still allowed.
    /// </summary>
    private void SaveDurationPresetEdits()
    {
        var editingIndex = _editingDurationPresetIndex!.Value;

        bool IsSlotAllZero(int index) =>
            _stagedDurationPresetHours[index] == 0 && _stagedDurationPresetMinutes[index] == 0 && _stagedDurationPresetSeconds[index] == 0;

        var isAnyOtherSlotInvalid = false;
        for (var i = 0; i < _stagedDurationPresetHours.Length; i++)
        {
            if (i != editingIndex && IsSlotAllZero(i))
            {
                isAnyOtherSlotInvalid = true;
                break;
            }
        }

        if (isAnyOtherSlotInvalid)
        {
            AutoStopDurationDescription.Visibility = Visibility.Collapsed;
            AutoStopDurationPresetZeroWarning.Text = "Please fix the invalid presets.";
            AutoStopDurationPresetZeroWarning.Visibility = Visibility.Visible;
            return;
        }

        if (IsSlotAllZero(editingIndex))
        {
            AutoStopDurationDescription.Visibility = Visibility.Collapsed;
            AutoStopDurationPresetZeroWarning.Text = "Enter a duration greater than 0.";
            AutoStopDurationPresetZeroWarning.Visibility = Visibility.Visible;
            return;
        }

        Array.Copy(_stagedDurationPresetHours, _durationPresetHours, _durationPresetHours.Length);
        Array.Copy(_stagedDurationPresetMinutes, _durationPresetMinutes, _durationPresetMinutes.Length);
        Array.Copy(_stagedDurationPresetSeconds, _durationPresetSeconds, _durationPresetSeconds.Length);
        ConfigService.Update(c =>
        {
            c.AutoStopDurationPresetHours = (int[])_durationPresetHours.Clone();
            c.AutoStopDurationPresetMinutes = (int[])_durationPresetMinutes.Clone();
            c.AutoStopDurationPresetSeconds = (int[])_durationPresetSeconds.Clone();
        });
        FinishDurationPresetEditMode(
            _durationPresetHours[editingIndex],
            _durationPresetMinutes[editingIndex],
            _durationPresetSeconds[editingIndex]);
    }

    /// <summary>
    /// Duration counterpart of FinishCountPresetEditMode, restoring all three duration fields/staged
    /// values at once (same DependencyProperty same-value-skips-ValueChanged reasoning applies here,
    /// hence the direct _stagedStopDurationHours/Minutes/Seconds assignments below alongside the
    /// NumberBox.Value ones). Also unconditionally restores AutoStopDurationDescription/
    /// AutoStopDurationPresetZeroWarning to their non-error state, rather than relying solely on the
    /// box-restoring assignments to trigger that via ValueChanged, since a same-value restore would
    /// otherwise leave a stale warning visible after edit mode has ended.
    /// </summary>
    private void FinishDurationPresetEditMode(int restoreHours, int restoreMinutes, int restoreSeconds)
    {
        SetPresetEditButtonsVisibility(AutoStopDurationEditButton, AutoStopDurationConfirmEditButton, AutoStopDurationCancelEditButton, AutoStopDurationResetPresetsButton, isEditing: false);
        SetPresetGridStretch(AutoStopDurationPresetGrid, stretch: false);
        AutoStopDurationPresetZeroWarning.Visibility = Visibility.Collapsed;
        AutoStopDurationDescription.Visibility = Visibility.Visible;
        AutoStopDurationDescription.Text = "Stops automation after this much time has elapsed.";
        AutoStopDurationDescription.FontWeight = FontWeights.Normal;
        foreach (var preset in _durationPresetButtons)
        {
            preset.IsChecked = false;
        }

        _editingDurationPresetIndex = null;
        RefreshDurationPresetLabels();
        AutoStopDurationHoursBox.Value = restoreHours;
        AutoStopDurationMinutesBox.Value = restoreMinutes;
        AutoStopDurationSecondsBox.Value = restoreSeconds;
        _stagedStopDurationHours = restoreHours;
        _stagedStopDurationMinutes = restoreMinutes;
        _stagedStopDurationSeconds = restoreSeconds;
        UpdateAutoStopDialogDefaultButtonState();
    }

    /// <summary>
    /// Duration counterpart of AbandonCountPresetEditIfActive.
    /// </summary>
    private void AbandonDurationPresetEditIfActive()
    {
        if (_editingDurationPresetIndex is null)
        {
            return;
        }

        FinishDurationPresetEditMode(_preEditDurationHours, _preEditDurationMinutes, _preEditDurationSeconds);
    }

    /// <summary>
    /// Duration counterpart of AutoStopCountCancelEditButton_Click.
    /// </summary>
    private void AutoStopDurationCancelEditButton_Click(object sender, RoutedEventArgs e)
    {
        AbandonDurationPresetEditIfActive();
    }

    /// <summary>
    /// Duration counterpart of AutoStopCountResetPresetsButton_Click, across all three duration
    /// fields/staged arrays at once. Refocuses AutoStopDurationHoursBox specifically, matching
    /// SelectDurationPresetForEditing's Hours-first focus convention.
    /// </summary>
    private void AutoStopDurationResetPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig();
        Array.Copy(defaults.AutoStopDurationPresetHours, _stagedDurationPresetHours, _stagedDurationPresetHours.Length);
        Array.Copy(defaults.AutoStopDurationPresetMinutes, _stagedDurationPresetMinutes, _stagedDurationPresetMinutes.Length);
        Array.Copy(defaults.AutoStopDurationPresetSeconds, _stagedDurationPresetSeconds, _stagedDurationPresetSeconds.Length);
        RefreshDurationPresetButtons(_stagedDurationPresetHours, _stagedDurationPresetMinutes, _stagedDurationPresetSeconds, isEditing: true);

        var index = _editingDurationPresetIndex!.Value;
        AutoStopDurationHoursBox.Value = _stagedDurationPresetHours[index];
        AutoStopDurationMinutesBox.Value = _stagedDurationPresetMinutes[index];
        AutoStopDurationSecondsBox.Value = _stagedDurationPresetSeconds[index];
        AutoStopDurationHoursBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Swaps which of a preset row's Edit button and its Confirm/Cancel/Reset trio are visible - Edit
    /// alone while not editing, Confirm+Cancel+Reset while mid-edit. Edit and Confirm are separate plain
    /// Buttons, each with a fixed appearance set directly in XAML, so there's nothing to swap except
    /// which buttons are visible.
    /// </summary>
    private static void SetPresetEditButtonsVisibility(Button editButton, Button confirmButton, Button cancelButton, Button resetButton, bool isEditing)
    {
        editButton.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        var editingVisibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        confirmButton.Visibility = editingVisibility;
        cancelButton.Visibility = editingVisibility;
        resetButton.Visibility = editingVisibility;
    }

    /// <summary>
    /// Keeps AutoStopDialog.DefaultButton at Primary (Enter anywhere in the dialog invokes OK, the
    /// dialog's original/normal behavior) except while either preset row is actually mid-edit
    /// (_editingCountPresetIndex/_editingDurationPresetIndex non-null - the authoritative state, not a
    /// ToggleButton's IsChecked now that Edit/Confirm are separate plain Buttons), when it's forced to
    /// None instead - otherwise the same Enter keystroke AutoStopPresetField_KeyDown already handles
    /// (saving that row's edit) would ALSO reach ContentDialog's own default-button activation and close
    /// the whole dialog on top of it. Called from every point either row's edit mode can start or end
    /// (Enter*/Finish*PresetEditMode), rather than computed once, since either row toggling independently
    /// must be reflected immediately.
    /// </summary>
    private void UpdateAutoStopDialogDefaultButtonState()
    {
        var isEditingEither = _editingCountPresetIndex is not null || _editingDurationPresetIndex is not null;
        AutoStopDialog.DefaultButton = isEditingEither ? ContentDialogButton.None : ContentDialogButton.Primary;
    }

    /// <summary>
    /// Shared PreviewKeyDown handler for AutoStopCountBox/AutoStopDurationHoursBox/MinutesBox/
    /// SecondsBox - while that box's row is actively editing a preset, Enter confirms/saves the row's
    /// edit instead of reaching AutoStopDialog's own DefaultButton handling, which would otherwise close
    /// the whole dialog on the same keystroke.
    /// Deliberately tunneling PreviewKeyDown, not bubbling KeyDown: NumberBox's inner "InputBox" TextBox
    /// handles Enter itself and marks that routed KeyDown Handled, so a bubbling handler on the NumberBox
    /// never sees it. PreviewKeyDown tunnels root-to-leaf before that bubbling phase starts, so this
    /// handler sees Enter first, guaranteed. Marking e.Handled = true also suppresses NumberBox's own
    /// commit step, which is fine since HookCountPresetLiveSync/HookDurationPresetLiveSync already keep
    /// the staged preset value correct on every keystroke independently of NumberBox's Value/Text.
    /// Escape mirrors Enter but calls Abandon* instead of Save*. This alone does not reliably stop
    /// AutoStopDialog's own Escape-dismiss on the same keystroke - see AutoStopDialog_Closing for the
    /// safety net that cancels the close outright while either row is still mid-edit.
    /// </summary>
    private void AutoStopPresetField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (_editingCountPresetIndex is not null)
            {
                SaveCountPresetEdits();
                e.Handled = true;
            }
            else if (_editingDurationPresetIndex is not null)
            {
                SaveDurationPresetEdits();
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (_editingCountPresetIndex is not null)
            {
                AbandonCountPresetEditIfActive();
                e.Handled = true;
            }
            else if (_editingDurationPresetIndex is not null)
            {
                AbandonDurationPresetEditIfActive();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// LosingFocus handler for AutoStopCountContainer - the second half of the fix described on
    /// SelectCountPresetForEditing: that method covers focus not being in AutoStopCountBox when Escape
    /// is pressed; this covers focus leaving the row's controls entirely some other way while still
    /// mid-edit (otherwise a stray Escape would fall through to ContentDialog's own Escape-dismiss and
    /// close the whole dialog). Any focus change leaving this row's subtree is treated as an implicit
    /// Cancel.
    /// LosingFocus is a bubbling routed event, so wiring it once on this container still sees every
    /// focus change originating anywhere inside it, without catching unrelated focus traffic elsewhere.
    /// IsWithin walks NewFocusedElement's ancestor chain: true means focus is moving to another control
    /// still inside this row (must not abandon); false (including null) means focus left the row
    /// entirely (must abandon). AbandonCountPresetEditIfActive is already a no-op when not editing.
    /// Deferred one dispatcher tick via DispatcherQueue.TryEnqueue rather than called directly: calling
    /// it synchronously crashes the app when this LosingFocus fires as a side effect of the dialog's own
    /// OK/Cancel button stealing focus while AutoStopDialog is tearing down its Popup at the same
    /// moment - WinUI's focus manager doesn't tolerate that kind of synchronous UI mutation mid-transition.
    /// </summary>
    private void AutoStopCountContainer_LosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        if (!IsWithin(e.NewFocusedElement as DependencyObject, AutoStopCountContainer))
        {
            DispatcherQueue.TryEnqueue(AbandonCountPresetEditIfActive);
        }
    }

    /// <summary>
    /// Duration counterpart of AutoStopCountContainer_LosingFocus, scoped to
    /// AutoStopDurationContainer/AbandonDurationPresetEditIfActive instead.
    /// </summary>
    private void AutoStopDurationContainer_LosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        if (!IsWithin(e.NewFocusedElement as DependencyObject, AutoStopDurationContainer))
        {
            DispatcherQueue.TryEnqueue(AbandonDurationPresetEditIfActive);
        }
    }

    /// <summary>
    /// Walks <paramref name="element"/>'s visual-tree ancestor chain looking for
    /// <paramref name="container"/>, returning true the moment it's found. A null
    /// <paramref name="element"/> always returns false.
    /// </summary>
    private static bool IsWithin(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void StopDatePicker_DateChanged(object sender, DatePickerValueChangedEventArgs args)
    {
        _stagedStopDate = args.NewDate.Date;
        AutoStopDateTimeDescription.Visibility = Visibility.Visible;
        AutoStopDateTimePastWarning.Visibility = Visibility.Collapsed;
    }

    private void StopTimePicker_TimeChanged(object sender, TimePickerValueChangedEventArgs args)
    {
        _stagedStopTime = args.NewTime;

        // While AutoStopPickDateToggle is Off, StopDatePicker is a disabled preview of the recurring
        // stop's next occurrence - re-resolved here so it reflects whatever time was just picked. Left
        // untouched while On, since there StopDatePicker is the user's own authoritative date choice.
        if (!AutoStopPickDateToggle.IsOn)
        {
            _stagedStopDate = ResolveNextOccurrenceDate(_stagedStopTime);
            StopDatePicker.Date = new DateTimeOffset(_stagedStopDate);
        }

        AutoStopDateTimeDescription.Visibility = Visibility.Visible;
        AutoStopDateTimePastWarning.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Commit-time clamp+truncation for the three "period of time" duration NumberBoxes, shared by all
    /// three like AdvancedIntervalBox_ValueChanged shares across its own four fields. Unlike
    /// AutoStopCountBox_ValueChanged, NaN is coerced straight to 0 here: these three fields default to 0
    /// for any component the user doesn't fill in. Otherwise clamps and truncates to a whole number,
    /// writing back only if it differs. Whichever box fired is identified by ReferenceEquals, so the
    /// corrected value lands in the matching _stagedStopDurationHours/Minutes/Seconds field, only
    /// committed into _stopDuration on OK. Also unconditionally restores AutoStopDurationDescription/
    /// Collapses AutoStopDurationZeroWarning on every edit, so a stale error from a failed OK disappears
    /// as soon as the user starts changing the field again.
    /// </summary>
    private void AutoStopDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double truncated;
        if (double.IsNaN(sender.Value))
        {
            truncated = 0;
            sender.Value = 0;
        }
        else
        {
            var clamped = Math.Clamp(sender.Value, sender.Minimum, sender.Maximum);
            truncated = TruncateToFractionDigits(clamped, fractionDigits: 0);
            if (truncated != sender.Value)
            {
                sender.Value = truncated;
            }
        }

        // Edit mode: these three boxes are previewing/editing one specific preset slot, not the real
        // configured duration - route the commit into that slot's staged trio and button label instead
        // of _stagedStopDurationHours/Minutes/Seconds. Also restores AutoStopDurationDescription/
        // AutoStopDurationPresetZeroWarning once the staged trio is genuinely non-zero again, so a stale
        // warning from a blocked SaveDurationPresetEdits disappears as soon as the user edits the value.
        // Deliberately re-checks all-zero rather than clearing unconditionally: NumberBox re-fires
        // ValueChanged for the same still-zero value while committing on Enter, which would otherwise
        // hide the just-shown rejection warning a split second after showing it.
        if (_editingDurationPresetIndex is int editingIndex)
        {
            if (ReferenceEquals(sender, AutoStopDurationHoursBox))
            {
                _stagedDurationPresetHours[editingIndex] = (int)truncated;
            }
            else if (ReferenceEquals(sender, AutoStopDurationMinutesBox))
            {
                _stagedDurationPresetMinutes[editingIndex] = (int)truncated;
            }
            else
            {
                _stagedDurationPresetSeconds[editingIndex] = (int)truncated;
            }

            // Goes through the shared RefreshDurationPresetButtons helper so this field's own preset gets
            // the same 100h+ cap and hide-seconds-with-"+" treatment every other render path gets.
            // Re-renders all three buttons, not just editingIndex's - the hide-seconds rule is a
            // row-wide condition, so this field's commit can flip it for the other two presets too.
            RefreshDurationPresetButtons(_stagedDurationPresetHours, _stagedDurationPresetMinutes, _stagedDurationPresetSeconds, isEditing: true);

            if (_stagedDurationPresetHours[editingIndex] != 0 || _stagedDurationPresetMinutes[editingIndex] != 0 || _stagedDurationPresetSeconds[editingIndex] != 0)
            {
                AutoStopDurationPresetZeroWarning.Visibility = Visibility.Collapsed;
                AutoStopDurationDescription.Visibility = Visibility.Visible;
            }

            return;
        }

        if (ReferenceEquals(sender, AutoStopDurationHoursBox))
        {
            _stagedStopDurationHours = (int)truncated;
        }
        else if (ReferenceEquals(sender, AutoStopDurationMinutesBox))
        {
            _stagedStopDurationMinutes = (int)truncated;
        }
        else
        {
            _stagedStopDurationSeconds = (int)truncated;
        }

        AutoStopDurationDescription.Visibility = Visibility.Visible;
        AutoStopDurationZeroWarning.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Rejects OK (args.Cancel = true) for two independent cases, each Collapsing its mode's caption and
    /// showing a warning in its place - the only place either warning is shown, not merely on a field
    /// losing focus:
    /// - Duration selected and all three staged fields are 0 -> AutoStopDurationZeroWarning (also
    ///   force-collapses AutoStopDurationPresetZeroWarning, since OK is reachable by mouse even mid-edit).
    /// - Date &amp; time selected, AutoStopPickDateToggle On, and the staged date+time isn't in the future
    ///   -> AutoStopDateTimePastWarning. Toggle Off (the Time-equivalent state) never validates past-time.
    /// Every other case closes normally - never blocks Count (Minimum="1" already makes all-zero
    /// unreachable there). AutoStopButton_Click's commit block separately guards _stopDuration against an
    /// all-zero TimeSpan when Duration is left at 0 but a different mode gets confirmed instead.
    /// </summary>
    private void AutoStopDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var selectedIndex = AutoStopSegmentedControl.SelectedIndex;

        var isDurationSelected = selectedIndex == 1;
        var isAllZero = _stagedStopDurationHours == 0 && _stagedStopDurationMinutes == 0 && _stagedStopDurationSeconds == 0;

        if (isDurationSelected && isAllZero)
        {
            args.Cancel = true;
            AutoStopDurationDescription.Visibility = Visibility.Collapsed;
            AutoStopDurationPresetZeroWarning.Visibility = Visibility.Collapsed;
            AutoStopDurationZeroWarning.Visibility = Visibility.Visible;
            return;
        }

        var isDateTimeSelected = selectedIndex == 0 && AutoStopPickDateToggle.IsOn;
        var isPast = _stagedStopDate.Add(_stagedStopTime) <= DateTime.Now;

        if (isDateTimeSelected && isPast)
        {
            args.Cancel = true;
            AutoStopDateTimeDescription.Visibility = Visibility.Collapsed;
            AutoStopDateTimePastWarning.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Fires once this dialog is actually about to close (never for a rejected OK), for any of the
    /// three ways it can close: OK, Cancel, or light-dismiss. The single spot that silently abandons an
    /// in-progress preset edit in either row without persisting it. Both Abandon* calls are no-ops if
    /// that row isn't currently editing, so this is safe to call unconditionally.
    /// </summary>
    private void AutoStopDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        AbandonCountPresetEditIfActive();
        AbandonDurationPresetEditIfActive();
    }

    /// <summary>
    /// Refreshes AutoStopButtonLabel.Text and AutoStopIcon.Symbol to reflect the currently committed
    /// _autoStopMode: the "Configure" placeholder if never configured; FormatAutoStopDateTime's
    /// relative-day summary for DateTime; "After N click(s)/jiggle(s)" (via FormatActionCount) for
    /// Count; FormatAutoStopDuration's terse "Stop in Xh Ym Zs" for Duration; or the same relative-day
    /// summary for Time, against the next actual occurrence of _timeStopTime, recomputed fresh from live
    /// DateTime.Now every call (unlike UpdateRunningTimeTargetTimeLabel's frozen _timeStopDeadline while
    /// a run is in progress). This is the static, not-running display - ApplyEngineStatus's own
    /// UpdateRunningDurationTargetTimeLabel overwrites this while a Duration run is in progress,
    /// reverting automatically once the run stops.
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
            case AutoStopMode.Duration:
                AutoStopButtonLabel.Text = $"Stop in {FormatAutoStopDuration(_stopDuration)}";
                AutoStopIcon.Glyph = "\uE916"; //Stopwatch
                break;
            case AutoStopMode.Time:
                AutoStopButtonLabel.Text = FormatAutoStopDateTime(ResolveNextOccurrenceDate(_timeStopTime) + _timeStopTime);
                AutoStopIcon.Glyph = "\uE917"; //AlarmClock
                break;
            default:
                AutoStopButtonLabel.Text = "Set a stop condition";
                AutoStopIcon.Glyph = "\uE93A"; //MiniExpand
                break;
        }
    }

    /// <summary>
    /// Summary of an Auto Stop Duration mode's TimeSpan for AutoStopButtonLabel's static display (used
    /// as "Stop in {this}"). When exactly one of hours/minutes/seconds is nonzero, spells it out in full
    /// with singular/plural wording ("2 hours"/"1 hour") instead of the abbreviated letter form, so it
    /// reads unambiguously as a single configured value rather than "2h 0m" looking deliberate. Once two
    /// or more components are nonzero, falls back to the terse "Xh Ym Zs" form, dropping a leading
    /// "0 &lt;unit&gt;" and a trailing zero seconds (but keeping "0m" if it sits between two nonzero
    /// components, e.g. "2h 0m 15s"). A fully zero duration falls back to "0s" - a deliberately allowed
    /// configuration; AutoStopDialog shows AutoStopDurationZeroWarning as a heads-up instead of
    /// rejecting it.
    /// useShortSingleUnitForm (used only by RenderDurationPresetButton, for the preset row's compact
    /// buttons) swaps the single-component branch's full-word phrasing for the terse "4h" shorthand
    /// instead, preserving the preset row's original compact labels.
    /// </summary>
    private static string FormatAutoStopDuration(TimeSpan duration, bool useShortSingleUnitForm = false)
    {
        // TotalHours, not TimeSpan.Hours - AutoStopDurationHoursBox allows configuring far more than 24
        // hours, and TimeSpan.Hours would only be the 0-23 remainder once the total reaches a full day.
        var hours = (int)duration.TotalHours;
        var nonZeroCount = (hours > 0 ? 1 : 0) + (duration.Minutes > 0 ? 1 : 0) + (duration.Seconds > 0 ? 1 : 0);

        if (nonZeroCount == 1)
        {
            if (useShortSingleUnitForm)
            {
                if (hours > 0)
                {
                    return $"{hours}h";
                }

                if (duration.Minutes > 0)
                {
                    return $"{duration.Minutes}m";
                }

                return $"{duration.Seconds}s";
            }

            if (hours > 0)
            {
                return FormatUnitWord(hours, "hour");
            }

            if (duration.Minutes > 0)
            {
                return FormatUnitWord(duration.Minutes, "minute");
            }

            return FormatUnitWord(duration.Seconds, "second");
        }

        var secondsSuffix = duration.Seconds > 0 ? $" {duration.Seconds}s" : string.Empty;

        if (hours > 0)
        {
            return $"{hours}h {duration.Minutes}m{secondsSuffix}";
        }

        if (duration.Minutes > 0)
        {
            return $"{duration.Minutes}m{secondsSuffix}";
        }

        return duration.Seconds > 0 ? $"{duration.Seconds}s" : "0s";
    }

    /// <summary>
    /// "{value} {unit}" with an "s" appended to unit unless value is exactly 1 - e.g. FormatUnitWord(1,
    /// "hour") is "1 hour", FormatUnitWord(5, "hour") is "5 hours". Shared by FormatAutoStopDuration's
    /// three single-field-configured cases (hour/minute/second).
    /// </summary>
    private static string FormatUnitWord(int value, string unit) => $"{value} {unit}{(value == 1 ? string.Empty : "s")}";

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
    /// (Re)schedules _autoStopLabelMidnightTimer to fire once at the next local midnight. Recomputes
    /// the exact interval fresh every time rather than assuming a fixed 24h, so it stays correct across
    /// DST transitions. Runs unconditionally regardless of _autoStopMode.
    /// </summary>
    private void ScheduleNextMidnightRefresh()
    {
        var now = DateTime.Now;
        _autoStopLabelMidnightTimer.Interval = now.Date.AddDays(1) - now;
        _autoStopLabelMidnightTimer.Start();
    }
}
