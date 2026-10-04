using XIVault.Core;
using XIVault.Core.Configuration;
using XIVault.Tests.Support;

namespace XIVault.Tests.Core;

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
        Assert.Equal(Path.Combine(env.OneDrive!, "XIVault"), config.BackupDestination);
        Assert.False(config.Schedule.Enabled);
    }

    [Fact]
    public void Without_OneDrive_the_default_destination_is_Documents()
    {
        using var env = new TestEnvironment(withOneDrive: false);
        Assert.Equal(Path.Combine(env.Documents, "XIVault"), new ConfigStore(env).Load().BackupDestination);
    }

    [Fact]
    public void Settings_round_trip_through_camel_case_json()
    {
        using var env = new TestEnvironment();
        var store = new ConfigStore(env);
        store.Save(new XivaultConfig
        {
            BackupDestination = @"D:\XIVault",
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
        Assert.Equal(@"D:\XIVault", loaded.BackupDestination);
        Assert.Equal(7, loaded.RetentionCount);
        Assert.True(loaded.IncludeDalamudUi);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], loaded.Schedule.Days);
        Assert.Equal(new TimeOnly(21, 30), loaded.Schedule.TimeOfDay);
    }

    [Fact]
    public void Malformed_json_is_an_invalid_configuration()
    {
        using var env = new TestEnvironment();
        var store = new ConfigStore(env);
        Directory.CreateDirectory(env.DataDirectory);
        File.WriteAllText(store.ConfigPath, "{ not json");

        var error = Assert.Throws<XivaultException>(store.Load);
        Assert.Equal(XivaultErrorKind.InvalidConfiguration, error.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Retention_outside_the_allowed_range_is_rejected(int retention)
    {
        using var env = new TestEnvironment();
        var error = Assert.Throws<XivaultException>(() => new ConfigStore(env).Save(new XivaultConfig { RetentionCount = retention }));
        Assert.Equal(XivaultErrorKind.InvalidConfiguration, error.Kind);
    }

    [Fact]
    public void Relative_destination_is_rejected()
    {
        using var env = new TestEnvironment();
        var error = Assert.Throws<XivaultException>(() => new ConfigStore(env).Save(new XivaultConfig { BackupDestination = "backups" }));
        Assert.Equal(XivaultErrorKind.InvalidConfiguration, error.Kind);
    }
}
