using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Scheduling;
using XivVault.Desktop.Controls;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;

namespace XivVault.Tests.Desktop;

public class OverviewViewModelTests
{
    [Fact]
    public async Task Shows_not_detected_until_XIVLauncher_exists()
    {
        using var host = new DesktopTestHost();
        var overview = host.Get<OverviewViewModel>();

        await overview.ActivateAsync();

        Assert.Equal(OverviewState.NotDetected, overview.State);
        Assert.True(overview.ShowNotDetected);
        Assert.Contains(overview.StatusRow, item => item is { Tone: Tone.Critical, Label: "XIVLauncher not found" });
    }

    [Fact]
    public async Task Backup_started_then_succeeded()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var overview = host.Get<OverviewViewModel>();
        await overview.ActivateAsync();
        Assert.Equal(OverviewState.Empty, overview.State);
        Assert.Contains("5 plugin configurations", overview.EmptyDescription);

        var stages = new List<OverviewState>();
        overview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverviewViewModel.State))
            {
                stages.Add(overview.State);
            }
        };
        await overview.BackUpNowCommand.ExecuteAsync(null);

        Assert.Contains(OverviewState.BackingUp, stages);
        Assert.Equal(OverviewState.JustBackedUp, overview.State);
        Assert.Equal("Backed up", overview.BadgeLabel);
        Assert.Equal("Just now", overview.LastBackupLabel);
        Assert.Equal("5", overview.PluginCount);
        Assert.Equal("Verified", overview.IntegrityLabel);
        Assert.Single(overview.Recent);
        Assert.Null(overview.ErrorMessage);
    }

    [Fact]
    public async Task Backup_progress_is_reported_while_it_runs()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var overview = host.Get<OverviewViewModel>();
        await overview.ActivateAsync();
        var labels = new List<string>();
        overview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverviewViewModel.StageLabel))
            {
                labels.Add(overview.StageLabel);
            }
        };

        await overview.BackUpNowCommand.ExecuteAsync(null);

        Assert.Contains("Verifying hashes", labels);
        Assert.Contains(labels, label => label.StartsWith("Compressing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_failed_shows_the_reason_and_keeps_the_state()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var freeLetter = "QRSTUVWXYZ".First(letter => !Directory.Exists($"{letter}:\\"));
        var store = host.Get<IConfigStore>();
        store.Save(store.Load() with { BackupDestination = $@"{freeLetter}:\XIV Vault" });
        var overview = host.Get<OverviewViewModel>();
        await overview.ActivateAsync();

        await overview.BackUpNowCommand.ExecuteAsync(null);

        Assert.Equal(OverviewState.Empty, overview.State);
        Assert.Contains("not available", overview.ErrorMessage);
        Assert.False(host.Session.IsBackingUp);
    }

    [Fact]
    public async Task A_damaged_latest_backup_needs_attention()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var latest = Directory.GetFiles(host.BackupFolder, "*.zip").Single();
        Core.RetentionTests.Corrupt(latest);
        var overview = host.Get<OverviewViewModel>();

        await overview.ActivateAsync();

        Assert.Equal(OverviewState.NeedsAttention, overview.State);
        Assert.True(overview.ShowFailed);
        Assert.Equal(Path.GetFileName(latest), overview.FailedMeta);
        Assert.Equal("Failed", overview.IntegrityLabel);
    }
}

public class BackupsViewModelTests
{
    [Fact]
    public async Task Loads_backups_with_totals_and_filters()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        await host.BackUpAsync();
        var backups = host.Get<BackupsViewModel>();

        await backups.ActivateAsync();

        Assert.Equal(2, backups.Rows.Count);
        Assert.StartsWith("2 backups", backups.Summary);
        Assert.Contains("keeping latest 10", backups.Summary);
        Assert.All(backups.Rows, row => Assert.Equal("Verified", row.IntegrityLabel));

        backups.Filter = backups.Filters.Single(filter => filter.Value == BackupFilter.Safety);
        Assert.Empty(backups.Rows);
        Assert.True(backups.IsEmpty);
        Assert.Equal("No backups match this filter", backups.EmptyTitle);
    }

    [Fact]
    public async Task Delete_asks_first_and_removes_only_when_confirmed()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var backups = host.Get<BackupsViewModel>();
        await backups.ActivateAsync();
        var row = backups.Rows.Single();

        host.Dialogs.Answer = false;
        await backups.DeleteCommand.ExecuteAsync(row);
        Assert.True(File.Exists(row.FilePath));

        host.Dialogs.Answer = true;
        await backups.DeleteCommand.ExecuteAsync(row);
        Assert.False(File.Exists(row.FilePath));
        Assert.Empty(backups.Rows);
        Assert.Equal(2, host.Dialogs.Requests.Count);
        Assert.True(host.Dialogs.Requests[0].Danger);
    }

    [Fact]
    public async Task Opening_the_page_verifies_only_the_latest_backup()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        await host.BackUpAsync();
        host.Get<XivVault.Core.State.IStateStore>().Update(state =>
        {
            state.Verifications.Clear();
            return state;
        });
        var backups = host.Get<BackupsViewModel>();

        await backups.ActivateAsync();

        Assert.Equal(["Verified", "Unverified"], backups.Rows.Select(row => row.IntegrityLabel));
    }

    [Fact]
    public async Task An_online_only_backup_is_shown_as_in_the_cloud_without_being_opened()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        host.AddCloudFile("xiv-vault-2026-10-03-080000.zip");
        var backups = host.Get<BackupsViewModel>();

        await backups.ActivateAsync();

        var row = Assert.Single(backups.Rows);
        Assert.Equal("In the cloud", row.IntegrityLabel);
        Assert.Equal("Not downloaded", row.PluginConfigsLabel);
        Assert.Equal("Backup", row.TypeLabel);
        Assert.True(row.CanRestore);
    }

    [Fact]
    public async Task Inspect_reveal_and_restore_act_on_the_row()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var backups = host.Get<BackupsViewModel>();
        await backups.ActivateAsync();
        var row = backups.Rows.Single();
        AppPage? navigated = null;
        host.Get<Navigator>().Navigated += (page, _) => navigated = page;

        backups.InspectCommand.Execute(row);
        backups.RevealCommand.Execute(row);
        backups.RestoreCommand.Execute(row);

        Assert.True(row.IsExpanded);
        Assert.Contains("Artisan", row.ChipItems);
        Assert.Equal([row.FilePath], host.Shell.Revealed);
        Assert.Equal(AppPage.Restore, navigated);
    }
}

public class RestoreViewModelTests
{
    [Fact]
    public async Task Walks_through_the_wizard_and_restores()
    {
        using var host = new DesktopTestHost();
        var launcher = host.CreateLauncher();
        await host.BackUpAsync();
        launcher.WritePluginConfig("Splatoon", "changed");
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);

        Assert.True(restore.IsStep1);
        Assert.NotNull(restore.Selected);

        await restore.ContinueCommand.ExecuteAsync(null);
        Assert.True(restore.IsStep2);
        Assert.Equal("5 plugin configurations", restore.PluginsLabel);
        Assert.Contains(restore.DalamudDetails, item => item is { Included: false, Label: "UI layout — not included" });
        Assert.NotEmpty(restore.Current);

        await restore.ContinueCommand.ExecuteAsync(null);
        Assert.True(restore.IsStep3);
        Assert.Equal(5, restore.Checks.Count);
        Assert.True(restore.ChecksPassed);
        Assert.True(restore.CanRestore);

        await restore.RestoreNowCommand.ExecuteAsync(null);
        Assert.True(restore.IsStep4);
        Assert.Equal("5 plugin configurations restored", restore.ResultPlugins);
        Assert.StartsWith("Safety snapshot created", restore.ResultSnapshot);
        Assert.All(restore.Stages, stage => Assert.Equal(StepState.Done, stage.State));
        Assert.NotEqual("changed", launcher.ReadPluginConfig("Splatoon"));
        Assert.False(host.Session.IsRestoring);
    }

    [Fact]
    public async Task A_backup_in_the_cloud_is_downloaded_before_the_review_step()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var path = Directory.GetFiles(host.BackupFolder).Single();
        host.MoveToCloud(path);
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(path);

        Assert.True(restore.ShowCloudNote);
        Assert.Contains(XivVault.Core.Formatting.Bytes(new FileInfo(path).Length), restore.CloudNoteText);
        Assert.Equal("Download and continue", restore.ContinueLabel);

        await restore.ContinueCommand.ExecuteAsync(null);

        Assert.True(restore.IsStep2);
        Assert.Equal("5 plugin configurations", restore.PluginsLabel);
        Assert.Equal(5, restore.Selected!.PluginCount);
        Assert.False(restore.IsDownloading);
    }

    [Fact]
    public async Task A_download_that_fails_stays_on_the_first_step_and_says_why()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var path = Directory.GetFiles(host.BackupFolder).Single();
        host.MoveToCloud(path);
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(path);
        using var unreachable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        await restore.ContinueCommand.ExecuteAsync(null);

        Assert.True(restore.IsStep1);
        Assert.Contains("online", restore.ErrorMessage);
        Assert.False(restore.IsDownloading);
    }

    [Fact]
    public async Task A_backup_that_appears_during_a_download_does_not_replace_the_one_being_downloaded()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var inCloud = Directory.GetFiles(host.BackupFolder).Single();
        host.MoveToCloud(inCloud);
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);
        host.Catalog.HoldDownloads();

        var continuing = restore.ContinueCommand.ExecuteAsync(null);
        await host.Catalog.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await host.BackUpAsync();
        await host.Session.RefreshAsync();
        host.Catalog.ReleaseDownloads();
        await continuing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(restore.IsStep2);
        Assert.Equal(inCloud, restore.Selected!.FilePath);
        Assert.Equal(inCloud, restore.Preview!.Backup.FilePath);
    }

    [Fact]
    public async Task Cancel_stops_a_download_and_stays_on_the_first_step()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        host.MoveToCloud(Directory.GetFiles(host.BackupFolder).Single());
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);
        host.Catalog.HoldDownloads();

        var continuing = restore.ContinueCommand.ExecuteAsync(null);
        await host.Catalog.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(restore.IsDownloading);
        restore.CancelDownloadCommand.Execute(null);
        await continuing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(restore.IsStep1);
        Assert.Null(restore.ErrorMessage);
        Assert.False(restore.IsDownloading);
        Assert.True(restore.ShowCloudNote);
    }

    [Fact]
    public async Task Choosing_another_backup_from_Backups_stops_a_download()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var inCloud = Directory.GetFiles(host.BackupFolder).Single();
        host.MoveToCloud(inCloud);
        await host.BackUpAsync();
        var local = Directory.GetFiles(host.BackupFolder).Single(path => path != inCloud);
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(inCloud);
        host.Catalog.HoldDownloads();

        var continuing = restore.ContinueCommand.ExecuteAsync(null);
        await host.Catalog.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        restore.Begin(local);
        await continuing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(restore.IsStep1);
        Assert.Null(restore.ErrorMessage);
        Assert.Equal(local, restore.Selected!.FilePath);
    }

    [Fact]
    public async Task The_review_starts_with_every_plugin_and_Dalamud_settings_chosen()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);

        Assert.All(restore.PluginChoices, choice => Assert.True(choice.IsChosen));
        Assert.Equal(5, restore.PluginChoices.Count);
        Assert.True(restore.DalamudSettingsChosen);
        Assert.Equal("5 plugin configurations", restore.PluginsLabel);
        Assert.Same(XivVault.Core.Restore.RestoreSelection.Everything, restore.Selection);
    }

    [Fact]
    public async Task Choosing_one_plugin_restores_only_that_plugin()
    {
        using var host = new DesktopTestHost();
        var launcher = host.CreateLauncher();
        var original = launcher.ReadPluginConfig("Artisan");
        await host.BackUpAsync();
        launcher.WritePluginConfig("Artisan", "changed");
        launcher.WritePluginConfig("Splatoon", "changed");
        launcher.Write("changed", "dalamudConfig.json");
        var restore = await ReviewAsync(host);

        restore.ChooseNoPluginsCommand.Execute(null);
        restore.PluginChoices.Single(choice => choice.Name == "Artisan").IsChosen = true;
        restore.DalamudSettingsChosen = false;
        Assert.Equal("1 of 5 plugin configurations", restore.PluginsLabel);
        Assert.StartsWith("Restores 1 plugin configuration from", restore.RestoreSummary);

        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.True(restore.IsStep4);
        Assert.Equal(original, launcher.ReadPluginConfig("Artisan"));
        Assert.Equal("changed", launcher.ReadPluginConfig("Splatoon"));
        Assert.Equal("changed", launcher.Read("dalamudConfig.json"));
        Assert.Equal("1 plugin configuration restored", restore.ResultPlugins);
        Assert.Equal("Dalamud configuration unchanged (not chosen)", restore.ResultDalamud);
    }

    [Fact]
    public async Task Continue_is_unavailable_while_nothing_is_chosen()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);

        restore.ChooseNoPluginsCommand.Execute(null);
        restore.DalamudSettingsChosen = false;
        Assert.False(restore.ContinueCommand.CanExecute(null));
        Assert.Equal("No plugin configurations", restore.PluginsLabel);

        restore.DalamudSettingsChosen = true;
        Assert.True(restore.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_older_backup_warning_counts_only_what_is_chosen()
    {
        using var host = new DesktopTestHost();
        var launcher = host.CreateLauncher();
        await host.BackUpAsync();
        launcher.WritePluginConfig("Splatoon", "changed");
        File.SetLastWriteTimeUtc(Path.Combine(launcher.PluginConfigs, "Splatoon.json"), host.Clock.Now.UtcDateTime.AddHours(1));
        var restore = await ReviewAsync(host);
        Assert.True(restore.ShowOlderWarning);
        Assert.Contains("Splatoon", restore.OlderWarningText);

        restore.PluginChoices.Single(choice => choice.Name == "Splatoon").IsChosen = false;

        Assert.False(restore.ShowOlderWarning);
    }

    [Fact]
    public async Task The_filter_narrows_the_plugin_list_without_changing_choices()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);

        restore.PluginFilter = "auto";

        Assert.Equal(["AutoRetainer"], restore.VisiblePluginChoices.Select(choice => choice.Name));
        Assert.All(restore.PluginChoices, choice => Assert.True(choice.IsChosen));
        restore.PluginFilter = "";
        Assert.Equal(5, restore.VisiblePluginChoices.Count);
    }

    [Fact]
    public async Task Select_all_and_none_act_on_the_plugins_the_filter_shows()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);

        restore.PluginFilter = "auto";
        restore.ChooseNoPluginsCommand.Execute(null);

        Assert.Equal(["AutoRetainer"], restore.PluginChoices.Where(choice => !choice.IsChosen).Select(choice => choice.Name));
        restore.ChooseAllPluginsCommand.Execute(null);
        Assert.All(restore.PluginChoices, choice => Assert.True(choice.IsChosen));
    }

    [Fact]
    public async Task Leaving_out_Dalamud_settings_shows_the_UI_layout_kept_as_is()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var config = host.Get<IConfigStore>();
        config.Save(config.Load() with { IncludeDalamudUi = true });
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);
        Assert.Equal("will be replaced", restore.Current.Single(item => item.Label == "UI layout").Meta);

        restore.DalamudSettingsChosen = false;

        Assert.Equal("kept as is", restore.Current.Single(item => item.Label == "UI layout").Meta);
    }

    [Fact]
    public async Task The_summary_says_whether_Dalamud_settings_are_restored()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);
        Assert.StartsWith("Restores 5 plugin configurations and Dalamud settings from", restore.RestoreSummary);

        restore.DalamudSettingsChosen = false;
        Assert.StartsWith("Restores 5 plugin configurations from", restore.RestoreSummary);

        restore.DalamudSettingsChosen = true;
        restore.ChooseNoPluginsCommand.Execute(null);
        Assert.StartsWith("Restores Dalamud settings from", restore.RestoreSummary);
    }

    [Fact]
    public async Task A_Dalamud_only_restore_reports_plugin_configurations_unchanged()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);
        restore.ChooseNoPluginsCommand.Execute(null);

        await restore.ContinueCommand.ExecuteAsync(null);
        await restore.RestoreNowCommand.ExecuteAsync(null);

        Assert.Equal("Plugin configurations unchanged (not chosen)", restore.ResultPlugins);
        Assert.Equal("Dalamud configuration restored", restore.ResultDalamud);
    }

    [Fact]
    public async Task The_review_says_why_Continue_is_unavailable()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = await ReviewAsync(host);
        Assert.Null(restore.ChoiceProblem);

        restore.ChooseNoPluginsCommand.Execute(null);
        restore.DalamudSettingsChosen = false;

        Assert.Equal("Choose at least one plugin or setting to restore.", restore.ChoiceProblem);
    }

    private static async Task<RestoreViewModel> ReviewAsync(DesktopTestHost host)
    {
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);
        await restore.ContinueCommand.ExecuteAsync(null);
        Assert.True(restore.IsStep2);
        return restore;
    }

    [Fact]
    public async Task The_newest_backup_stays_selected_until_one_is_chosen()
    {
        // The wizard is built when the app opens; a backup made afterwards must become the default.
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        var first = restore.Selected!.FilePath;

        await host.BackUpAsync();
        await host.Session.RefreshAsync();

        Assert.NotEqual(first, restore.Selected!.FilePath);
        Assert.Equal(restore.Choices[0].FilePath, restore.Selected.FilePath);
    }

    [Fact]
    public async Task A_backup_the_user_chose_stays_selected_when_a_new_one_appears()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        await host.BackUpAsync();
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        var older = restore.Choices[1].FilePath;
        restore.Selected = restore.Choices[1];

        await host.BackUpAsync();
        await host.Session.RefreshAsync();

        Assert.Equal(older, restore.Selected!.FilePath);
    }

    [Fact]
    public async Task Safety_checks_block_the_restore_while_the_game_runs()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        host.Processes.Running.Add("ffxiv_dx11");
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);
        await restore.ContinueCommand.ExecuteAsync(null);

        await restore.ContinueCommand.ExecuteAsync(null);

        Assert.False(restore.ChecksPassed);
        Assert.False(restore.CanRestore);
        Assert.Contains(restore.Checks, check => check is { Tone: Tone.Critical, Label: "FFXIV closed" });
        Assert.Contains("Close XIVLauncher and FFXIV", restore.ErrorMessage);
    }

    [Fact]
    public async Task A_backup_chosen_from_Backups_is_preselected()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        await host.BackUpAsync();
        await host.Session.RefreshAsync();
        var older = host.Session.Status!.Backups[^1].FilePath;
        var restore = host.Get<RestoreViewModel>();

        restore.Begin(older);

        Assert.Equal(older, restore.Selected!.FilePath);
    }

    [Fact]
    public async Task A_backup_file_from_elsewhere_can_be_chosen()
    {
        using var host = new DesktopTestHost();
        var oldPc = Support.FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "OldPC", "XIVLauncher"));
        var backups = host.Get<IBackupService>();
        var external = Path.Combine(host.Environment.Root, "USB");
        var made = await backups.CreateBackupAsync(new BackupRequest(BackupKind.Manual) { Source = oldPc.Root, Destination = external }, cancellationToken: TestContext.Current.CancellationToken);
        host.Picker.File = made.Record.FilePath;
        var restore = host.Get<RestoreViewModel>();
        await host.Session.RefreshAsync();
        restore.Begin(null);
        Assert.False(restore.HasChoices);

        await restore.ChooseFileCommand.ExecuteAsync(null);

        Assert.Equal(made.Record.FilePath, restore.Selected!.FilePath);
    }
}

public class DiagnosticsViewModelTests
{
    [Fact]
    public async Task Shows_the_five_groups_and_copies_a_report()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        await host.BackUpAsync();
        var diagnostics = host.Get<DiagnosticsViewModel>();

        await diagnostics.ActivateAsync();

        Assert.Equal(["XIVLauncher", "Dalamud", "Game settings", "Backup destination", "Scheduling"], diagnostics.Groups.Select(group => group.Title));
        Assert.Contains("passed", diagnostics.Subtitle);
        Assert.True(diagnostics.HasBanner);
        var copy = diagnostics.CopyReportCommand.ExecuteAsync(null);
        Assert.True(diagnostics.Copied);
        Assert.StartsWith("XIV Vault diagnostic report", host.Clipboard.Text);
        await copy;
        Assert.False(diagnostics.Copied);
    }
}

public class SettingsViewModelTests
{
    [Fact]
    public async Task Changes_are_saved_and_survive_a_restart()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var settings = host.Get<SettingsViewModel>();
        await settings.ActivateAsync();

        settings.IncludeUi = true;
        settings.Compression = settings.CompressionOptions.Single(option => option.Value == CompressionPreset.Maximum);
        settings.RetentionUpCommand.Execute(null);
        host.Picker.Folder = Path.Combine(host.Environment.Root, "NAS");
        await settings.ChooseFolderCommand.ExecuteAsync(null);

        var saved = host.Get<IConfigStore>().Load();
        Assert.True(saved.IncludeDalamudUi);
        Assert.Equal(CompressionPreset.Maximum, saved.Compression);
        Assert.Equal(11, saved.RetentionCount);
        Assert.Equal(Path.Combine(host.Environment.Root, "NAS"), saved.BackupDestination);

        var reopened = new SettingsViewModel(host.Get<IConfigStore>(), host.Get<XivVault.Core.Discovery.IXivLauncherLocator>(), host.Picker, host.Session, host.Get<UpdatesViewModel>(), host.Get<XivVault.Core.Platform.PathDisplay>());
        reopened.Load();
        Assert.True(reopened.IncludeUi);
        Assert.Equal(11, reopened.Retention);
    }

    [Fact]
    public async Task Constructing_the_screen_never_overwrites_saved_settings()
    {
        using var host = new DesktopTestHost();
        var store = host.Get<IConfigStore>();
        store.Save(store.Load() with { Compression = CompressionPreset.Fast, IncludeDalamudUi = true });

        var settings = host.Get<SettingsViewModel>();
        await settings.ActivateAsync();

        Assert.Equal(CompressionPreset.Fast, store.Load().Compression);
        Assert.Equal(CompressionPreset.Fast, settings.Compression.Value);
    }

    [Fact]
    public async Task An_override_must_point_at_a_real_XIVLauncher_folder()
    {
        using var host = new DesktopTestHost();
        host.CreateLauncher();
        var settings = host.Get<SettingsViewModel>();
        await settings.ActivateAsync();
        settings.ToggleOverrideCommand.Execute(null);

        var empty = Path.Combine(host.Environment.Root, "Empty");
        Directory.CreateDirectory(empty);
        settings.OverrideText = empty;
        await settings.UseOverrideCommand.ExecuteAsync(null);
        Assert.NotNull(settings.OverrideError);
        Assert.Null(host.Get<IConfigStore>().Load().XivLauncherPathOverride);

        var custom = Support.FakeXivLauncher.Create(Path.Combine(host.Environment.Root, "Games", "XIVLauncher"));
        settings.OverrideText = custom.Root;
        await settings.UseOverrideCommand.ExecuteAsync(null);
        Assert.Equal(custom.Root, host.Get<IConfigStore>().Load().XivLauncherPathOverride);
        Assert.Equal("Custom", settings.DetectedLabel);

        await settings.ResetOverrideCommand.ExecuteAsync(null);
        Assert.Null(host.Get<IConfigStore>().Load().XivLauncherPathOverride);
    }
}

public class ScheduleViewModelTests
{
    private static readonly ScheduledCommand Existing = new(@"C:\Apps\XIV Vault\XIV-Vault.exe", "--scheduled-backup");

    [Fact]
    public async Task Opening_the_screen_leaves_an_existing_task_alone()
    {
        using var host = new DesktopTestHost();
        var xml = TaskXml.Build(new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Time = "21:00" }, Existing, @"PC\u", DateTime.Now);
        host.Commands.Handler = (_, args) => args[0] == "/Query" ? new(0, xml, "") : new(0, "", "");

        var schedule = host.Get<ScheduleViewModel>();
        await schedule.ActivateAsync();

        Assert.DoesNotContain(host.Commands.Calls, call => call.Arguments[0] is "/Create" or "/Delete");
        Assert.True(schedule.AutoEnabled);
        Assert.Equal(ScheduleFrequency.Daily, schedule.Frequency.Value);
        Assert.Equal("21:00", schedule.SelectedTime.Value);
    }

    [Fact]
    public async Task Turning_automatic_backups_on_and_off_installs_and_removes_the_task()
    {
        using var host = new DesktopTestHost();
        string? xml = null;
        host.Commands.Handler = (_, args) =>
        {
            switch (args[0])
            {
                case "/Create":
                    xml = File.ReadAllText(args[4]);
                    return new(0, "", "");
                case "/Delete":
                    xml = null;
                    return new(0, "", "");
                default:
                    return xml is null ? new(1, "", "not found") : new(0, xml, "");
            }
        };
        var schedule = host.Get<ScheduleViewModel>();
        await schedule.ActivateAsync();
        Assert.False(schedule.AutoEnabled);

        schedule.AutoEnabled = true;
        await schedule.ApplyAsync();
        Assert.NotNull(xml);
        Assert.Contains("--scheduled-backup", xml);
        Assert.Equal("Oct 11 · 12:00 PM", schedule.NextLabel);
        Assert.True(host.Get<IConfigStore>().Load().Schedule.Enabled);

        await schedule.ToggleDayCommand.ExecuteAsync(schedule.Weekdays.Single(day => day.Day == DayOfWeek.Wednesday));
        Assert.Contains("<Wednesday />", xml);

        schedule.AutoEnabled = false;
        await schedule.ApplyAsync();
        Assert.Null(xml);
        Assert.Equal("Paused", schedule.NextLabel);
    }
}
