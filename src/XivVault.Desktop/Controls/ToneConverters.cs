using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XivVault.Desktop.Controls;

public static class ToneConverters
{
    public static readonly IValueConverter Main = new FuncValueConverter<Tone, IBrush>(ToneBrushes.Base);

    public static readonly IValueConverter Text = new FuncValueConverter<Tone, IBrush>(ToneBrushes.Text);

    public static readonly IValueConverter Subtle = new FuncValueConverter<Tone, IBrush>(ToneBrushes.Subtle);

    public static readonly IValueConverter Border = new FuncValueConverter<Tone, IBrush>(ToneBrushes.Border);
}
