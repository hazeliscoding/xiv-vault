using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using XIVault.Desktop.ViewModels;

namespace XIVault.Desktop.Views;

public static class Converters
{
    public static readonly IValueConverter Upper =
        new FuncValueConverter<string?, string?>(text => text?.ToUpperInvariant());

    public static readonly IValueConverter IsLoading =
        new FuncValueConverter<OverviewState, bool>(state => state == OverviewState.Loading);

    public static readonly IValueConverter IsJustBackedUp =
        new FuncValueConverter<OverviewState, bool>(state => state == OverviewState.JustBackedUp);

    /// <summary>Accent edge when on, hairline when off, as on the Automatic Backups card.</summary>
    public static readonly IValueConverter AccentBorderWhen =
        new FuncValueConverter<bool, IBrush?>(on => Resource(on ? "AccentBorder" : "Border1"));

    /// <summary>The schedule options fade back when automatic backups are off.</summary>
    public static readonly IValueConverter DimWhenOff = new FuncValueConverter<bool, double>(on => on ? 1 : 0.45);

    /// <summary>A hairline above every row but the first.</summary>
    public static readonly IValueConverter TopHairline =
        new FuncValueConverter<bool, Thickness>(divided => divided ? new Thickness(0, 1, 0, 0) : default);

    public static readonly IValueConverter IsDone =
        new FuncValueConverter<StepState, bool>(state => state == StepState.Done);

    public static readonly IValueConverter IsNotDone =
        new FuncValueConverter<StepState, bool>(state => state != StepState.Done);

    public static readonly IValueConverter IsCurrent =
        new FuncValueConverter<StepState, bool>(state => state == StepState.Current);

    public static readonly IValueConverter IsTodo =
        new FuncValueConverter<StepState, bool>(state => state == StepState.Todo);

    private static IBrush? Resource(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as IBrush : null;
}
