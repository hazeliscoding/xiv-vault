using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace XIVault.Desktop.Controls;

/// <summary>
/// The design system's button: classes pick the variant (primary, secondary, ghost, danger) and
/// size (sm, lg); "icon" makes it square for icon-only use.
/// </summary>
public class XvButton : Button
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<XvButton, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> IconRightProperty =
        AvaloniaProperty.Register<XvButton, string?>(nameof(IconRight));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<XvButton, bool>(nameof(IsLoading));

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? IconRight
    {
        get => GetValue(IconRightProperty);
        set => SetValue(IconRightProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLoadingProperty)
        {
            PseudoClasses.Set(":loading", IsLoading);
        }
    }
}

/// <summary>The design system's on/off switch: a 28x16 track with a sliding knob.</summary>
public class XvSwitch : ToggleButton;
