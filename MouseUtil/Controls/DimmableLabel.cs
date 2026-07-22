using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MouseUtil.Controls;

/// <summary>
/// A plain, non-interactive text label whose look participates in the standard Control
/// "Normal"/"Disabled" CommonStates VisualStateManager group, so toggling <see cref="Control.IsEnabled"/>
/// swaps it between whichever Foreground each state's Setter declares - exactly like any built-in
/// Control's disabled look, including staying correctly themed if the app/OS theme changes while
/// either state is active, since each Setter targets a live {ThemeResource}.
///
/// Exists because plain TextBlock isn't a Control at all - it has no IsEnabled, no
/// VisualStateManager, nothing to hook a disabled look into. IntervalCaptionTextBlock/
/// HotkeyCaptionTextBlock in MainWindow.xaml used to be plain TextBlocks whose "disabled" look was
/// faked by MainWindow.xaml.cs's SetInputsEnabled manually copying a Brush *instance* (snapshotted at
/// that moment from a hidden ThemeResource-bound helper TextBlock) into Foreground every time the
/// enabled/disabled state changed. That snapshot never updated itself again until the next
/// enabled/disabled transition, so it went stale the moment the theme changed in between - this
/// Control-based replacement removes the snapshot (and the staleness bug) entirely by letting WinUI's
/// own template/VisualState machinery reapply {ThemeResource} colors, the same way it does for any
/// other Control.
///
/// Unlike PointerOver/Pressed (which a generic Control does NOT manage on its own - individual
/// controls like Button hook their own pointer events for those), the Normal/Disabled transition here
/// is driven explicitly below via the IsEnabledChanged event, since WinUI's base Control class does
/// not wire that up automatically for arbitrary custom controls either.
///
/// Also resyncs on every <see cref="FrameworkElement.Loaded"/>, not just the first
/// <see cref="OnApplyTemplate"/> - this matters for an instance living inside a Flyout/Popup (e.g.
/// HotkeyCaptionTextBlock in MainWindow.xaml, inside SettingsFlyout): OnApplyTemplate only ever runs
/// ONCE per instance, the first time it's ever realized, but a Flyout's content is disconnected from
/// the live visual tree every time it closes and reconnected (raising Loaded again) every time it
/// reopens. A GoToState call made via IsEnabledChanged while the Flyout is closed does not reliably
/// stick - there's nothing rendering the transition - so without this, an instance whose IsEnabled
/// flips while its Flyout happens to be closed (the overwhelmingly common case - closing the Flyout
/// is exactly how the user gets back to the Start/Stop button) would keep showing whatever state
/// happened to be applied the very first time it was ever shown, indefinitely. Re-running
/// UpdateVisualState on every Loaded re-reads the CURRENT IsEnabled and re-applies the correct look
/// immediately whenever the label becomes visible again, regardless of how it got there. A label that
/// is never inside a closable container (e.g. IntervalCaptionTextBlock, always part of the main
/// window's permanently-live content) never hits this path and is unaffected either way.
/// </summary>
public sealed class DimmableLabel : Control
{
    public DimmableLabel()
    {
        // Purely decorative text - never part of tab order or a hit-test/pointer target, matching
        // the plain TextBlock this replaces (which was never focusable or interactive either).
        IsTabStop = false;
        IsHitTestVisible = false;

        IsEnabledChanged += (_, _) => UpdateVisualState(useTransitions: true);
        Loaded += (_, _) => UpdateVisualState(useTransitions: false);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // No transition animation on initial load - land directly in the correct state.
        UpdateVisualState(useTransitions: false);
    }

    private void UpdateVisualState(bool useTransitions) =>
        VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", useTransitions);
}
