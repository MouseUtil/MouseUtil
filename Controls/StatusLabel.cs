using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MouseUtil.Controls;

/// <summary>
/// The set of color "tones" StatusLabel's status line can render in - one VisualState per tone (see
/// StatusLabelStyle in MainWindow.xaml). Deliberately named after intent (Muted/Accent/Success/
/// Critical/Caution) rather than after a specific brush, since the actual color each one maps to is
/// entirely the ControlTemplate's concern.
/// </summary>
public enum StatusTone
{
    Muted,
    Accent,
    Success,
    Critical,
    Caution
}

/// <summary>
/// Templated status-line Control whose Foreground is driven by a "Tone" VisualStateManager state
/// group instead of a code-behind-assigned Brush instance. Replaces the old
/// MainWindow.xaml.cs SetStatusText(string text, Brush foreground) pattern, where every call site had
/// to snapshot one of MutedBrushSource/AccentBrushSource/SuccessBrushSource/CriticalBrushSource/
/// CautionBrushSource's already-resolved Foreground Brush - a copy that never updated again and so
/// went stale on the next theme change, until whatever next event happened to call SetStatusText.
///
/// Callers now just set <see cref="Text"/> and <see cref="Tone"/> (a plain enum); each Tone's
/// VisualState.Setter in the ControlTemplate references a live {ThemeResource}, so WinUI reapplies
/// the theme-correct color automatically - including immediately on a theme change while a non-Muted
/// tone is active - the same way it already does for a disabled Button.
/// </summary>
public sealed class StatusLabel : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusLabel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(StatusTone), typeof(StatusLabel),
        new PropertyMetadata(StatusTone.Muted, OnToneChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusTone Tone
    {
        get => (StatusTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // No transition animation on initial load - land directly in whichever tone is already set.
        UpdateVisualState(useTransitions: false);
    }

    private static void OnToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusLabel)d).UpdateVisualState(useTransitions: true);

    private void UpdateVisualState(bool useTransitions) =>
        VisualStateManager.GoToState(this, Tone.ToString(), useTransitions);
}
