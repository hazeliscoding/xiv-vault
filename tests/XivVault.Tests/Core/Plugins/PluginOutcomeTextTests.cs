using XivVault.Core.Plugins;

namespace XivVault.Tests.Core.Plugins;

public class PluginOutcomeTextTests
{
    [Fact]
    public void Every_outcome_has_its_own_calm_phrase()
    {
        var phrases = Enum.GetValues<PluginInstallOutcome>().Select(PluginOutcomeText.Describe).ToList();

        Assert.All(phrases, phrase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(phrase));
            Assert.DoesNotContain("!", phrase, StringComparison.Ordinal);
            Assert.False(phrase.EndsWith('.'), phrase);
        });
        Assert.Equal(phrases.Count, phrases.Distinct().Count());
    }

    [Fact]
    public void Phrases_read_after_a_plugin_name()
    {
        Assert.Equal("installed", PluginOutcomeText.Describe(PluginInstallOutcome.Installed));
        Assert.Equal("needs a newer version of Dalamud", PluginOutcomeText.Describe(PluginInstallOutcome.NeedsNewerDalamud));
    }
}
