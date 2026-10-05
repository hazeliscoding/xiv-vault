using XivVault.Core;
using XivVault.Core.Configuration;
using XivVault.Tests.Support;

namespace XivVault.Tests.Core;

public class ConfigStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults_with_a_OneDrive_destination()
    {
        using var env = new TestEnvironment(withOneDrive: true);
        var config = new ConfigStore(env).Load();

        Assert.Equal(10, config.RetentionCount);
        Assert.False(config.IncludeDalamudUi);
        Assert.Equal(CompressionPreset.Balanced, config.Compression);
        Assert.Equal(Path.Combine(env.OneDrive!, "XIV Vault"), config.BackupDestination);
        Assert.False(config.Schedule.Enabled);
    }

    [Fact]
    public void Without_OneDrive_the_default_destination_is_Documents()
    {
        using var env = new TestEnvironment(withOneDrive: false);
        Assert.Equal(Path.Combine(env.Documents, "XIV Vault"), new ConfigStore(env).Load().BackupDestination);
    }

    [Fact]
    public void Settings_round_trip_through_camel_case_json()
    {
        using var env = new TestEnvironment();
        var store = new ConfigStore(env);
        store.Save(new XivVaultConfig
        {
            BackupDestination = @"D:\XIV Vault",
            RetentionCount = 7,
            IncludeDalamudUi = true,
            Compression = CompressionPreset.Maximum,
            Schedule = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Friday], Time = "21:30" },
        });

        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"backupDestination\"", json);
        Assert.Contains("\"retentionCount\": 7", json);
        Assert.Contains("\"maximum\"", json);
        Assert.Contains("\"weekly\"", json);

        var loaded = store.Load();
        Assert.Equal(@"D:\XIV Vault", loaded.BackupDestination);
        Assert.Equal(7, loaded.RetentionCount);
        Assert.True(loaded.IncludeDalamudUi);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], loaded.Schedule.Days);
        Assert.Equal(new TimeOnly(21, 30), loaded.Schedule.TimeOfDay);
    }

    [Fact]
    public void Keys_missing_from_the_file_keep_their_defaults()
    {
        using var env = new TestEnvironment();
        var store = new ConfigStore(env);
        Directory.CreateDirectory(env.DataDirectory);
        File.WriteAllText(store.ConfigPath, "{ \"includeDalamudUi\": true, \"schedule\": { \"enabled\": true } }");

        var config = store.Load();

        Assert.True(config.IncludeDalamudUi);
        Assert.Equal(XivVaultConfig.DefaultRetentionCount, config.RetentionCount);
        Assert.Equal(CompressionPreset.Balanced, config.Compression);
        Assert.True(config.Schedule.Enabled);
        Assert.Equal(ScheduleFrequency.Weekly, config.Schedule.Frequency);
        Assert.Equal([DayOfWeek.Sunday], config.Schedule.Days);
        Assert.Equal("12:00", config.Schedule.Time);
    }

    [Fact]
    public void Update_checks_are_on_unless_turned_off()
    {
        using var env = new TestEnvironment();
        var store = new ConfigStore(env);
        Directory.CreateDirectory(env.DataDirectory);

        // Settings saved before the option existed have no key for it.
        File.WriteAllText(store.ConfigPath, "{ \"retentionCount\": 5 }");
        Assert.True(store.Load().CheckForUpdates);

        store.Save(store.Load() with { CheckForUpdates = false });
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(store.ConfigPath));
        Assert.False(store.Load().CheckForUpdates);
    }

    [Fact]
    public void Malformed_json_is_an_invalid_configuration()
    {
        using var env = new TestEnvironment();
        var store = new ConfigStore(env);
        Directory.CreateDirectory(env.DataDirectory);
        File.WriteAllText(store.ConfigPath, "{ not json");

        var error = Assert.Throws<XivVaultException>(store.Load);
        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Retention_outside_the_allowed_range_is_rejected(int retention)
    {
        using var env = new TestEnvironment();
        var error = Assert.Throws<XivVaultException>(() => new ConfigStore(env).Save(new XivVaultConfig { RetentionCount = retention }));
        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
    }

    [Fact]
    public void Relative_destination_is_rejected()
    {
        using var env = new TestEnvironment();
        var error = Assert.Throws<XivVaultException>(() => new ConfigStore(env).Save(new XivVaultConfig { BackupDestination = "backups" }));
        Assert.Equal(XivVaultErrorKind.InvalidConfiguration, error.Kind);
    }
}
