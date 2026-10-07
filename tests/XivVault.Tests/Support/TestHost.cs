using Microsoft.Extensions.DependencyInjection;
using XivVault.Core;
using XivVault.Core.Backup;
using XivVault.Core.Configuration;
using XivVault.Core.Discovery;
using XivVault.Core.Platform;
using XivVault.Core.Restore;
using XivVault.Core.State;

namespace XivVault.Tests.Support;

/// <summary>The real Core services, wired against a temp profile, fake processes and a test clock.</summary>
public sealed class TestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public TestHost(bool withOneDrive = true, Action<IServiceCollection>? configure = null)
    {
        Environment = new TestEnvironment(withOneDrive);
        var services = new ServiceCollection();
        services.AddSingleton<IAppEnvironment>(Environment);
        services.AddSingleton<IProcessInspector>(Processes);
        services.AddSingleton<ICommandRunner>(Commands);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IFileAvailability>(Files);
        configure?.Invoke(services);
        services.AddXivVaultCore();
        _provider = services.BuildServiceProvider();
    }

    public TestEnvironment Environment { get; }

    public FakeProcessInspector Processes { get; } = new();

    public FakeCommandRunner Commands { get; } = new();

    public FakeFileAvailability Files { get; } = new();

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 4, 13, 19, 0, TimeSpan.Zero));

    public IServiceProvider Services => _provider;

    public T Get<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    public IConfigStore Config => Get<IConfigStore>();

    public IBackupService Backups => Get<IBackupService>();

    public IBackupCatalog Catalog => Get<IBackupCatalog>();

    public IRestoreService Restores => Get<IRestoreService>();

    public IXivLauncherLocator Locator => Get<IXivLauncherLocator>();

    public IStateStore State => Get<IStateStore>();

    public string BackupFolder => Config.Load().BackupDestination!;

    public FakeXivLauncher CreateLauncher(bool userDataLayout = false, bool withUi = true) =>
        FakeXivLauncher.Create(Environment.XivLauncherPath, userDataLayout, withUi: withUi);

    public void UpdateConfig(Func<XivVaultConfig, XivVaultConfig> change) => Config.Save(change(Config.Load()));

    /// <summary>Creates a backup and moves the clock on, so every archive gets its own timestamp.</summary>
    public async Task<BackupResult> BackUpAsync(BackupKind kind = BackupKind.Manual)
    {
        var result = await Backups.CreateBackupAsync(new BackupRequest(kind), cancellationToken: TestContext.Current.CancellationToken);
        Clock.Advance(TimeSpan.FromMinutes(1));
        return result;
    }

    /// <summary>Makes a backup online only on a PC that has never verified it, as on a new PC.</summary>
    public void MoveToCloud(string path)
    {
        Files.OnlineOnly.Add(path);
        State.Update(state =>
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
}
