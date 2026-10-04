using CommunityToolkit.Mvvm.ComponentModel;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;

namespace XivVault.Desktop.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract AppPage Page { get; }

    /// <summary>Called each time the screen is shown, so it reflects changes made elsewhere.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;
}

/// <summary>A dot and a short phrase, as in the Overview's status row.</summary>
public sealed record StatusItem(Tone Tone, string Label);

/// <summary>A choice in a segmented control. ToString gives the label the control shows.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
