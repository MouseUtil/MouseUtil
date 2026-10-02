using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MouseUtil.Controls;

/// <summary>
/// Color "tones" StatusLabel's status line can render in - one VisualState per tone. Named after
/// intent rather than a specific brush; the actual color is the ControlTemplate's concern.
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
/// group instead of a code-behind-assigned Brush instance. Callers just set <see cref="Text"/> and
/// <see cref="Tone"/>; each Tone's VisualState.Setter references a live {ThemeResource}, so WinUI
/// reapplies the theme-correct color automatically, including on a theme change.
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
