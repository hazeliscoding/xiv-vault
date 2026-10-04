using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace XivVault.Desktop.Controls;

/// <summary>A small round status light.</summary>
public sealed class StatusDot : Control
{
    public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<StatusDot, Tone>(nameof(Tone));

    public static readonly StyledProperty<double> DiameterProperty = AvaloniaProperty.Register<StatusDot, double>(nameof(Diameter), 7);

    static StatusDot()
    {
        AffectsRender<StatusDot>(ToneProperty);
        AffectsMeasure<StatusDot>(DiameterProperty);
    }

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double Diameter
    {
        get => GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter);

    public override void Render(DrawingContext context)
    {
        var brush = ToneBrushes.Base(Tone);
        var radius = Diameter / 2;
        if (Tone == Tone.Paused)
        {
            context.DrawRectangle(brush, null, new Rect(0, 0, Diameter - 1, Diameter), 1, 1);
            return;
        }

        if (Tone == Tone.Unknown)
        {
            context.DrawEllipse(null, new Pen(brush, 1.5, new DashStyle([1.5, 1.5], 0)), new Point(radius, radius), radius - 0.75, radius - 0.75);
            return;
        }

        context.DrawEllipse(brush, null, new Point(radius, radius), radius, radius);
    }
}

/// <summary>Base for controls that expose the four brushes of their tone to a template.</summary>
public abstract class ToneControl : TemplatedControl
{
    public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<ToneControl, Tone>(nameof(Tone));

    public static readonly DirectProperty<ToneControl, IBrush?> ToneMainProperty =
        AvaloniaProperty.RegisterDirect<ToneControl, IBrush?>(nameof(ToneMain), control => control.ToneMain);

    public static readonly DirectProperty<ToneControl, IBrush?> ToneTextProperty =
        AvaloniaProperty.RegisterDirect<ToneControl, IBrush?>(nameof(ToneText), control => control.ToneText);

    public static readonly DirectProperty<ToneControl, IBrush?> ToneSubtleProperty =
        AvaloniaProperty.RegisterDirect<ToneControl, IBrush?>(nameof(ToneSubtle), control => control.ToneSubtle);

    public static readonly DirectProperty<ToneControl, IBrush?> ToneBorderProperty =
        AvaloniaProperty.RegisterDirect<ToneControl, IBrush?>(nameof(ToneBorder), control => control.ToneBorder);

    private IBrush? _main;
    private IBrush? _text;
    private IBrush? _subtle;
    private IBrush? _border;

    protected ToneControl() => UpdateBrushes();

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public IBrush? ToneMain
    {
        get => _main;
        private set => SetAndRaise(ToneMainProperty, ref _main, value);
    }

    public IBrush? ToneText
    {
        get => _text;
        private set => SetAndRaise(ToneTextProperty, ref _text, value);
    }

    public IBrush? ToneSubtle
    {
        get => _subtle;
        private set => SetAndRaise(ToneSubtleProperty, ref _subtle, value);
    }

    public IBrush? ToneBorder
    {
        get => _border;
        private set => SetAndRaise(ToneBorderProperty, ref _border, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty)
        {
            UpdateBrushes();
        }
    }

    private void UpdateBrushes()
    {
        ToneMain = ToneBrushes.Base(Tone);
        ToneText = ToneBrushes.Text(Tone);
        ToneSubtle = ToneBrushes.Subtle(Tone);
        ToneBorder = ToneBrushes.Border(Tone);
    }
}

/// <summary>A tinted pill with a dot and a short label, such as "Verified".</summary>
public sealed class StatusBadge : ToneControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<StatusBadge, string?>(nameof(Label));

    // Template text isn't visible to screen readers, so the badge carries its label as its name.
    public StatusBadge() => AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Text);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty)
        {
            AutomationProperties.SetName(this, Label);
        }
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}

/// <summary>A full-width message with a colored edge: info, warning or critical.</summary>
public sealed class Banner : ToneControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<Banner, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<Banner, string?>(nameof(Description));

    public static readonly StyledProperty<string?> MetaProperty = AvaloniaProperty.Register<Banner, string?>(nameof(Meta));

    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<Banner, string?>(nameof(Icon));

    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<Banner, object?>(nameof(Actions));

    public static readonly DirectProperty<Banner, string?> ResolvedIconProperty =
        AvaloniaProperty.RegisterDirect<Banner, string?>(nameof(ResolvedIcon), banner => banner.ResolvedIcon);

    public static readonly DirectProperty<Banner, IBrush?> TitleBrushProperty =
        AvaloniaProperty.RegisterDirect<Banner, IBrush?>(nameof(TitleBrush), banner => banner.TitleBrush);

    private string? _resolvedIcon;
    private IBrush? _titleBrush;

    public Banner()
    {
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Text);
        Tone = Tone.Info;
    }

    public IBrush? TitleBrush
    {
        get => _titleBrush;
        private set => SetAndRaise(TitleBrushProperty, ref _titleBrush, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Meta
    {
        get => GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public string? ResolvedIcon
    {
        get => _resolvedIcon;
        private set => SetAndRaise(ResolvedIconProperty, ref _resolvedIcon, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty || change.Property == IconProperty)
        {
            ResolvedIcon = Icon ?? Tone switch
            {
                Tone.Healthy => "Check",
                Tone.Warning => "TriangleAlert",
                Tone.Critical => "OctagonAlert",
                _ => "Info",
            };
            TitleBrush = Tone == Tone.Critical
                ? ToneBrushes.Text(Tone.Critical)
                : Application.Current?.TryGetResource("Text1", null, out var text) == true ? text as IBrush : null;

            // Problems are announced as soon as they appear; other messages wait for a pause.
            AutomationProperties.SetLiveSetting(this, Tone == Tone.Critical ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        }

        if (change.Property == TitleProperty || change.Property == DescriptionProperty)
        {
            AutomationProperties.SetName(this, string.Join(". ", new[] { Title, Description }.Where(text => !string.IsNullOrEmpty(text))));
        }
    }
}

/// <summary>A quiet one-line placeholder for empty lists.</summary>
public sealed class EmptyState : ToneControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Description));

    public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Icon), "Inbox");

    public EmptyState()
    {
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Text);
        Tone = Tone.Unknown;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == DescriptionProperty)
        {
            AutomationProperties.SetName(this, string.Join(". ", new[] { Title, Description }.Where(text => !string.IsNullOrEmpty(text))));
        }
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
