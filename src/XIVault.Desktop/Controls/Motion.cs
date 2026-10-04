using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace XIVault.Desktop.Controls;

/// <summary>The small spinner used inside loading buttons: a ring with one open side.</summary>
public sealed class Spinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<Spinner>();

    static Spinner() => AffectsRender<Spinner>(ForegroundProperty);

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var radius = (size - 1.5) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false);
            path.ArcTo(new Point(center.X - radius, center.Y), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            path.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Foreground ?? Brushes.Gray, 1.5), geometry);
    }
}

/// <summary>
/// The "aether ring" from the mockup's in-progress states: a spinning arc, a counter-rotating
/// dashed ring and a glowing diamond. Motion comes from styles in Motion.axaml, which are not
/// loaded when Windows animations are turned off.
/// </summary>
public sealed class AetherRing : Panel
{
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<AetherRing, double>(nameof(Size), 56);

    private readonly Panel _outer = new() { Classes = { "ring-outer" } };
    private readonly Ellipse _middle = new() { Classes = { "ring-middle" }, StrokeThickness = 1.5, StrokeDashArray = [2, 2.5] };
    private readonly Border _core = new() { Classes = { "ring-core" }, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(1) };

    public AetherRing()
    {
        _outer.Children.Add(new Ellipse { StrokeThickness = 2, Stroke = Resource("AccentBorder") });
        _outer.Children.Add(new Arc { StrokeThickness = 2, Stroke = Resource("Accent"), StartAngle = -135, SweepAngle = 90 });
        _middle.Stroke = new SolidColorBrush(Color.Parse("#B3B48EFF"));
        _core.BorderBrush = Resource("AccentText");
        _core.RenderTransform = new RotateTransform(45);
        Children.Add(_outer);
        Children.Add(_middle);
        Children.Add(_core);
        Apply();
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SizeProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Width = Height = Size;
        _middle.Margin = new Thickness(Size * 0.2);
        _core.Margin = new Thickness(Size * 0.38);
        _core.HorizontalAlignment = HorizontalAlignment.Stretch;
        _core.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private static IBrush Resource(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush brush ? brush : Brushes.Gray;
}

/// <summary>The animated dashed arrow between the three nodes of the Overview flow diagram.</summary>
public sealed class FlowLine : Canvas
{
    public FlowLine()
    {
        Width = 72;
        Height = 12;
        var brush = Application.Current?.TryGetResource("Accent", null, out var value) == true && value is IBrush accent ? accent : Brushes.CornflowerBlue;
        Children.Add(new Line
        {
            Classes = { "flow-dash" },
            StartPoint = new Point(2, 6),
            EndPoint = new Point(64, 6),
            Stroke = brush,
            StrokeThickness = 1,
            StrokeDashArray = [3, 4],
            Opacity = 0.7,
        });
        var dot = new Ellipse { Width = 3.2, Height = 3.2, Fill = brush };
        SetLeft(dot, 0.4);
        SetTop(dot, 4.4);
        Children.Add(dot);
        Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M63 2 L68 6 L63 10"),
            Stroke = brush,
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        });
    }
}
