using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace XivVault.Desktop.Controls;

/// <summary>A Lucide icon drawn as strokes, like the mockup's 1.75 px line icons.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 14);

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 1.75);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    private static readonly Dictionary<string, Geometry> Geometries = [];

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeWidthProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public static bool Exists(string kind) => LucideIcons.Paths.ContainsKey(kind);

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Kind is not { } kind || Foreground is not { } brush || !LucideIcons.Paths.TryGetValue(kind, out var data))
        {
            return;
        }

        if (!Geometries.TryGetValue(kind, out var geometry))
        {
            geometry = StreamGeometry.Parse(data);
            Geometries[kind] = geometry;
        }

        // Lucide draws on a 24 unit grid; scaling the stroke with it matches the SVG rendering.
        var scale = Size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
