using Microsoft.Extensions.DependencyInjection;
using XivVault.Core.Backup;
using XivVault.Core.Platform;
using XivVault.Core.Scheduling;
using XivVault.Desktop;
using XivVault.Desktop.Services;
using XivVault.Desktop.ViewModels;
using XivVault.Tests.Support;

namespace XivVault.Tests.Desktop;

/// <summary>The desktop's view models on real Core services, with every UI service faked.</summary>
public sealed class DesktopTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public DesktopTestHost()
    {
        Environment = new TestEnvironment();
        var services = new ServiceCollection();
        services.AddSingleton<IAppEnvironment>(Environment);
        services.AddSingleton<IProcessInspector>(Processes);
        services.AddSingleton<ICommandRunner>(Commands);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IFileAvailability>(Files);
        services.AddXivVaultDesktop();

        // Registered after the app's own, so these win.
        services.AddSingleton<IUiThread, InlineUiThread>();
        services.AddSingleton<IDialogService>(Dialogs);
        services.AddSingleton<IShellService>(Shell);
        services.AddSingleton<IClipboardService>(Clipboard);
        services.AddSingleton<IFilePicker>(Picker);
        services.AddSingleton<IMotionSettings, NoMotion>();
        services.AddSingleton<ISchedulerTarget, FakeSchedulerTarget>();
        services.AddSingleton<IAppUpdater>(Updater);
        services.AddSingleton<IAppInstances>(Instances);
        services.AddSingleton<IBackupCatalog>(provider => Catalog ??= new GatedCatalog(provider.GetRequiredService<BackupCatalog>()));
        _provider = services.BuildServiceProvider();
        Get<IBackupCatalog>();
    }

    public TestEnvironment Environment { get; }

    public FakeProcessInspector Processes { get; } = new();

    public FakeCommandRunner Commands { get; } = new();

    public FakeFileAvailability Files { get; } = new();

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 4, 13, 19, 0, TimeSpan.Zero));

    public FakeDialogs Dialogs { get; } = new();

    public FakeShell Shell { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakePicker Picker { get; } = new();

    public FakeUpdater Updater { get; } = new();

    public FakeInstances Instances { get; } = new();

    public GatedCatalog Catalog { get; private set; } = null!;

    public T Get<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    public DesktopSession Session => Get<DesktopSession>();

    public FakeXivLauncher CreateLauncher() => FakeXivLauncher.Create(Environment.XivLauncherPath);

    public FakeGameSettings CreateGameSettings() => FakeGameSettings.Create(Environment.Documents);

    public string BackupFolder => Get<XivVault.Core.Configuration.IConfigStore>().Load().BackupDestination!;

    public async Task BackUpAsync()
    {
        await Get<XivVault.Core.Backup.IBackupService>().CreateBackupAsync(new(XivVault.Core.Backup.BackupKind.Manual), cancellationToken: TestContext.Current.CancellationToken);
        Clock.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>Makes a backup online only on a PC that has never verified it, as on a new PC.</summary>
    public void MoveToCloud(string path)
    {
        Files.OnlineOnly.Add(path);
        Get<XivVault.Core.State.IStateStore>().Update(state =>
        {
            state.Verifications.Clear();
            return state;
        });
    }

    /// <summary>An online-only file whose bytes are junk, so reading it would show up as a damaged archive.</summary>
    public string AddCloudFile(string name)
    {
        Directory.CreateDirectory(BackupFolder);
        var path = Path.Combine(BackupFolder, name);
        File.WriteAllText(path, "not downloaded");
        Files.OnlineOnly.Add(path);
        return path;
    }

    public void Dispose()
    {
        _provider.Dispose();
        Environment.Dispose();
    }

    private sealed class InlineUiThread : IUiThread
    {
        public void Post(Action action) => action();
    }

    private sealed class NoMotion : IMotionSettings
    {
        public bool ReduceMotion => true;
    }

    private sealed class FakeSchedulerTarget : ISchedulerTarget
    {
        public ScheduledCommand Command { get; } = new(@"C:\Apps\XIV Vault\XIV-Vault.exe", "--scheduled-backup");
    }
}

/// <summary>The real catalog, except that a test can hold downloads until it lets them finish.</summary>
public sealed class GatedCatalog(BackupCatalog inner) : IBackupCatalog
{
    private TaskCompletionSource? _gate;

    /// <summary>Completes once a held download has started.</summary>
    public TaskCompletionSource DownloadStarted { get; private set; } = new();

    public void HoldDownloads()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseDownloads() => _gate?.TrySetResult();

    public BackupRecord Download(BackupRecord record, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        DownloadStarted.TrySetResult();
        _gate?.Task.Wait(cancellationToken);
        return inner.Download(record, progress, cancellationToken);
    }

    public IReadOnlyList<BackupRecord> List(string destination) => inner.List(destination);

    public IReadOnlyList<BackupRecord> ListVerifyingLatest(string destination, CancellationToken cancellationToken = default) =>
        inner.ListVerifyingLatest(destination, cancellationToken);

    public BackupRecord? Read(string archivePath) => inner.Read(archivePath);

    public BackupRecord Verify(BackupRecord record, CancellationToken cancellationToken = default) => inner.Verify(record, cancellationToken);

    public void Delete(BackupRecord record, string destination) => inner.Delete(record, destination);
}

public sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<ConfirmRequest> Requests { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(Answer);
    }
}

public sealed class FakeShell : IShellService
{
    public List<string> Revealed { get; } = [];

    public List<string> Opened { get; } = [];

    public List<string> Launched { get; } = [];

    public void RevealInExplorer(string path) => Revealed.Add(path);

    public bool Launch(string executable)
    {
        Launched.Add(executable);
        return true;
    }

    public void OpenUrl(string url) => Opened.Add(url);
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

public sealed class FakePicker : IFilePicker
{
    public string? Folder { get; set; }

    public string? File { get; set; }

    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(Folder);

    public Task<string?> PickBackupFileAsync(string? startFolder) => Task.FromResult(File);
}

/// <summary>An installed copy by default, with no newer version until <see cref="Latest"/> is set.</summary>
public sealed class FakeUpdater : IAppUpdater
{
    public bool IsInstalled { get; set; } = true;

    public string? Latest { get; set; }

    public Exception? CheckError { get; set; }

    public Exception? DownloadError { get; set; }

    /// <summary>Runs while the download is in progress.</summary>
    public Action? DuringDownload { get; set; }

    public int Checks { get; private set; }

    public List<string> Downloaded { get; } = [];

    public string? RestartedInto { get; private set; }

    public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        if (CheckError is { } error)
        {
            return Task.FromException<AvailableUpdate?>(error);
        }

        return Task.FromResult(Latest is null ? null : new AvailableUpdate(Latest));
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
    {
        progress(50);
        DuringDownload?.Invoke();
        if (DownloadError is { } error)
        {
            return Task.FromException(error);
        }

        progress(100);
        Downloaded.Add(update.Version);
        return Task.CompletedTask;
    }

    public void RestartToApply(AvailableUpdate update) => RestartedInto = update.Version;
}

public sealed class FakeInstances : IAppInstances
{
    public bool OthersRunning { get; set; }
}
