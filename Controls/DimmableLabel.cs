using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MouseUtil.Controls;

/// <summary>
/// A plain, non-interactive text label whose look participates in the standard Control
/// "Normal"/"Disabled" CommonStates VisualStateManager group, so toggling <see cref="Control.IsEnabled"/>
/// swaps its Foreground via a live {ThemeResource}-bound Setter - unlike plain TextBlock, which isn't
/// a Control and has no IsEnabled/VisualStateManager to hook into.
///
/// The Normal/Disabled transition is driven explicitly via the IsEnabledChanged event, since a generic
/// Control doesn't wire that up on its own. Also resyncs on every <see cref="FrameworkElement.Loaded"/>,
/// not just the first <see cref="OnApplyTemplate"/> - this matters for an instance inside a Flyout/Popup,
/// whose content disconnects and reconnects from the tree on every close/reopen (OnApplyTemplate only
/// ever runs once), so without it a state change made while the Flyout was closed wouldn't stick.
/// </summary>
public sealed class DimmableLabel : Control
{
    /// <summary>
    /// Optional override for the caption text, used only by IntervalCaptionLabelStyle's template (bound
    /// via TemplateBinding); other DimmableLabel styles hardcode their caption text instead since it
    /// never changes at runtime. Lets MainWindow's interval-preset edit mode swap the caption between
    /// "Interval"/"Edit preset"/"Add preset" without a separate TextBlock. Defaults to "Interval".
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(DimmableLabel), new PropertyMetadata("Interval"));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public DimmableLabel()
    {
        // Purely decorative text - never part of tab order or a hit-test/pointer target.
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
