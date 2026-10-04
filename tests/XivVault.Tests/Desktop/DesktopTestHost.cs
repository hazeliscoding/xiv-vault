using Microsoft.Extensions.DependencyInjection;
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
        services.AddXivVaultDesktop();

        // Registered after the app's own, so these win.
        services.AddSingleton<IUiThread, InlineUiThread>();
        services.AddSingleton<IDialogService>(Dialogs);
        services.AddSingleton<IShellService>(Shell);
        services.AddSingleton<IClipboardService>(Clipboard);
        services.AddSingleton<IFilePicker>(Picker);
        services.AddSingleton<IMotionSettings, NoMotion>();
        services.AddSingleton<ISchedulerTarget, FakeSchedulerTarget>();
        _provider = services.BuildServiceProvider();
    }

    public TestEnvironment Environment { get; }

    public FakeProcessInspector Processes { get; } = new();

    public FakeCommandRunner Commands { get; } = new();

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 4, 13, 19, 0, TimeSpan.Zero));

    public FakeDialogs Dialogs { get; } = new();

    public FakeShell Shell { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakePicker Picker { get; } = new();

    public T Get<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    public DesktopSession Session => Get<DesktopSession>();

    public FakeXivLauncher CreateLauncher() => FakeXivLauncher.Create(Environment.XivLauncherPath);

    public string BackupFolder => Get<XivVault.Core.Configuration.IConfigStore>().Load().BackupDestination!;

    public async Task BackUpAsync()
    {
        await Get<XivVault.Core.Backup.IBackupService>().CreateBackupAsync(new(XivVault.Core.Backup.BackupKind.Manual), cancellationToken: TestContext.Current.CancellationToken);
        Clock.Advance(TimeSpan.FromMinutes(1));
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
        public ScheduledCommand Command { get; } = new(@"C:\Apps\XIV Vault\XivVault.exe", "--scheduled-backup");
    }
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

    public List<string> Launched { get; } = [];

    public void RevealInExplorer(string path) => Revealed.Add(path);

    public bool Launch(string executable)
    {
        Launched.Add(executable);
        return true;
    }

    public void OpenUrl(string url)
    {
    }
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
