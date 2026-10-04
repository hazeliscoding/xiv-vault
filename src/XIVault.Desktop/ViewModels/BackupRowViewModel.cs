using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using XIVault.Core;
using XIVault.Core.Backup;
using XIVault.Desktop.Controls;

namespace XIVault.Desktop.ViewModels;

/// <summary>One backup as every list in the app shows it.</summary>
public sealed partial class BackupRowViewModel : ObservableObject
{
    private const int ChipCount = 8;

    public BackupRowViewModel(BackupRecord record, DateTime nowLocal) => Update(record, nowLocal);

    public BackupRecord Record { get; private set; } = null!;

    public string FilePath => Record.FilePath;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsVerifying { get; set; }

    public string Day { get; private set; } = "";

    public string Time { get; private set; } = "";

    public string FullDate { get; private set; } = "";

    public string Age { get; private set; } = "";

    public string Size { get; private set; } = "";

    public int PluginCount { get; private set; }

    public string PluginConfigsLabel => Formatting.Count(PluginCount, "plugin config");

    public string PluginSummary => $"{Formatting.Count(PluginCount, "plugin configuration")} · {Size}";

    public string TypeLabel { get; private set; } = "";

    public Tone TypeTone { get; private set; }

    public string IntegrityLabel => IsVerifying ? "Verifying" : Formatting.IntegrityLabel(Record.Integrity);

    public Tone IntegrityTone => IsVerifying ? Tone.Info : Record.Integrity switch
    {
        IntegrityState.Verified => Tone.Healthy,
        IntegrityState.Failed => Tone.Critical,
        _ => Tone.Unknown,
    };

    public string FileName => Record.FileName;

    public string HashLine => Record.ArchiveSha256 is { } sha ? "sha256 " + ArchiveValidator.Short(sha) : Record.Problem ?? "sha256 not checked yet";

    public string ContentsLine { get; private set; } = "";

    public IReadOnlyList<string> PluginChips { get; private set; } = [];

    public string? MoreChip { get; private set; }

    /// <summary>The chips shown when a row is inspected: the first plugin names, then "+N more".</summary>
    public IReadOnlyList<string> ChipItems => MoreChip is null ? PluginChips : [.. PluginChips, MoreChip];

    public bool IsSafety => Record.IsSafetySnapshot;

    public bool CanRestore => Record.HasManifest && Record.Integrity != IntegrityState.Failed;

    /// <summary>List items are announced by their ToString, so it reads like the row looks.</summary>
    public override string ToString() => AccessibleName;

    public string AccessibleName => $"{Day} {Time}, {TypeLabel}, {Size}, {PluginConfigsLabel}, {IntegrityLabel}";

    partial void OnIsVerifyingChanged(bool value)
    {
        OnPropertyChanged(nameof(IntegrityLabel));
        OnPropertyChanged(nameof(IntegrityTone));
    }

    public void Update(BackupRecord record, DateTime nowLocal)
    {
        Record = record;
        var local = record.CreatedAtUtc.ToLocalTime();
        Day = Formatting.Day(local, nowLocal);
        Time = Formatting.Time(local);
        FullDate = Formatting.FullDate(local);
        Age = Formatting.Age(local, nowLocal);
        Size = Formatting.Bytes(record.SizeBytes);
        PluginCount = record.PluginConfigCount;
        TypeLabel = Formatting.KindLabel(record.Kind, longForm: true);
        TypeTone = record.Kind switch
        {
            BackupKind.Manual => Tone.Accent,
            BackupKind.PreRestore => Tone.Queued,
            _ => Tone.Unknown,
        };

        var names = record.PluginNames;
        PluginChips = names.Take(ChipCount).ToList();
        MoreChip = names.Count > ChipCount ? string.Create(CultureInfo.InvariantCulture, $"+{names.Count - ChipCount} more") : null;

        var contents = new List<string>();
        if (record.Manifest is { } manifest)
        {
            if (manifest.Contents.DalamudConfig)
            {
                contents.Add("Dalamud settings");
            }

            if (manifest.Contents.DalamudVfs)
            {
                contents.Add("plugin collection db");
            }

            if (manifest.Contents.DalamudConfig)
            {
                contents.Add("custom repos");
            }

            if (manifest.Contents.DalamudUi)
            {
                contents.Add("UI layout");
            }
        }

        ContentsLine = contents.Count > 0 ? string.Join(" · ", contents) : "no Dalamud settings";
        OnPropertyChanged(string.Empty);
    }
}
