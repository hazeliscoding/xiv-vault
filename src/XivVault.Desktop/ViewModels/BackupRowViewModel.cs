using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Desktop.Controls;

namespace XivVault.Desktop.ViewModels;

/// <summary>One backup as every list in the app shows it.</summary>
public sealed partial class BackupRowViewModel : ObservableObject
{
    private const int ChipCount = 8;
    private const string NotDownloaded = "Not downloaded";

    public BackupRowViewModel(BackupRecord record, DateTime nowLocal) => Update(record, nowLocal);

    public BackupRecord Record { get; private set; } = null!;

    public string FilePath => Record.FilePath;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Day { get; private set; } = "";

    public string Time { get; private set; } = "";

    public string FullDate { get; private set; } = "";

    public string Age { get; private set; } = "";

    public string Size { get; private set; } = "";

    public int PluginCount { get; private set; }

    public string PluginConfigsLabel => Record.HasManifest ? Formatting.Count(PluginCount, "plugin config") : NotDownloaded;

    public string PluginSummary => $"{(Record.HasManifest ? Formatting.Count(PluginCount, "plugin configuration") : NotDownloaded)} · {Size}";

    public string PluginCountText => Record.HasManifest ? PluginCount.ToString(CultureInfo.InvariantCulture) : "–";

    public string TypeLabel { get; private set; } = "";

    public Tone TypeTone { get; private set; }

    public string IntegrityLabel => Formatting.IntegrityLabel(Record);

    public Tone IntegrityTone => IntegrityToneOf(Record);

    public string FileName => Record.FileName;

    public string HashLine => Record.ArchiveSha256 is { } sha ? "sha256 " + ArchiveValidator.Short(sha) : Record.Problem ?? "sha256 not checked yet";

    public string ContentsLine { get; private set; } = "";

    /// <summary>The game settings a backup holds. Characters are counted, never named.</summary>
    public string GameLine { get; private set; } = "";

    public IReadOnlyList<string> PluginChips { get; private set; } = [];

    public string? MoreChip { get; private set; }

    /// <summary>The chips shown when a row is inspected: the first plugin names, then "+N more".</summary>
    public IReadOnlyList<string> ChipItems => MoreChip is null ? PluginChips : [.. PluginChips, MoreChip];

    public bool IsSafety => Record.IsSafetySnapshot;

    public bool CanRestore => Record.IsRecognized && Record.Integrity != IntegrityState.Failed;

    /// <summary>List items are announced by their ToString, so it reads like the row looks.</summary>
    public override string ToString() => AccessibleName;

    public string AccessibleName => $"{Day} {Time}, {TypeLabel}, {Size}, {PluginConfigsLabel}, {IntegrityLabel}";

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
        TypeLabel = Formatting.KindLabel(record, longForm: true);
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

        var game = new List<string>();
        if (record.Manifest is { Contents.CharacterSettings: true })
        {
            game.Add(record.CharacterCount > 0 ? $"character settings for {Formatting.Count(record.CharacterCount, "character")}" : "character settings");
        }

        if (record.Manifest is { Contents.SystemSettings: true })
        {
            game.Add("system settings");
        }

        GameLine = string.Join(" · ", game);
        ContentsLine = !record.HasManifest && record.IsOnlineOnly ? "in the cloud · contents show once it is downloaded"
            : contents.Count > 0 ? string.Join(" · ", contents)
            : "no Dalamud settings";
        OnPropertyChanged(string.Empty);
    }

    public static Tone IntegrityToneOf(BackupRecord record) => record switch
    {
        { Integrity: IntegrityState.Verified } => Tone.Healthy,
        { Integrity: IntegrityState.Failed } => Tone.Critical,
        { IsOnlineOnly: true } => Tone.Info,
        _ => Tone.Unknown,
    };
}
