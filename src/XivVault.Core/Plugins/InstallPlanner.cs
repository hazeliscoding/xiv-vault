namespace XivVault.Core.Plugins;

internal static class InstallPlanner
{
    /// <summary>
    /// Decides, without any network or disk access, which plugins to install, from where and which
    /// build, and why each other plugin isn't installed.
    /// </summary>
    public static InstallPlan Plan(
        IReadOnlyList<PluginReference> plugins,
        InstalledPlugins installed,
        Version dalamudVersion,
        IReadOnlyList<RepositoryPlugin> official,
        IReadOnlyList<CustomRepositoryList> customRepositories,
        DalamudBanList bans,
        bool doPluginTest,
        IReadOnlySet<string> testingOptIns)
    {
        var api = dalamudVersion.Major;

        // Dalamud drops a custom entry whose name matches an official one in any case, even one the official
        // repository can't install (PluginRepository.cs L135-152), and hides custom entries marked hidden.
        // Official entries are never hidden: Dalamud ignores the flag, which kamori sets on most of them.
        var officialNames = official.Select(entry => entry.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var custom = customRepositories
            .Where(repository => repository.Plugins is not null)
            .Select(repository => (repository.Url, Entries: repository.Plugins!.Where(entry => !entry.IsHide && !officialNames.Contains(entry.InternalName)).ToList()))
            .ToList();
        var someUnavailable = customRepositories.Any(repository => repository.Plugins is null);

        var installs = new List<PlannedInstall>();
        var skipped = new List<PluginOutcome>();
        foreach (var plugin in plugins)
        {
            var outcome = Decide(plugin, out var install);
            if (install is not null)
            {
                installs.Add(install);
            }
            else
            {
                skipped.Add(new PluginOutcome(plugin.InternalName, outcome));
            }
        }

        return new InstallPlan(installs, skipped);

        // Installed with the install to plan, or why the plugin is skipped.
        PluginInstallOutcome Decide(PluginReference plugin, out PlannedInstall? install)
        {
            install = null;
            var name = plugin.InternalName;
            PluginInstallOutcome? blocked = installed.IsLink ? PluginInstallOutcome.LinkedFolder : installed.StateOf(name) switch
            {
                PluginFolderState.Installed => PluginInstallOutcome.AlreadyInstalled,
                PluginFolderState.InTheWay => PluginInstallOutcome.InTheWay,
                PluginFolderState.Link => PluginInstallOutcome.LinkedFolder,
                _ => installed.Ids.Contains(plugin.WorkingPluginId) ? PluginInstallOutcome.IdInUse : null,
            };
            if (blocked is { } reason)
            {
                return reason;
            }

            // An official entry is the only source for its name, whether or not it can be installed.
            string? repositoryUrl = null;
            var entry = official.FirstOrDefault(candidate => candidate.InternalName == name);
            if (entry is null)
            {
                var offers = custom.Where(repository => repository.Entries.Any(candidate => candidate.InternalName == name)).ToList();
                if (offers.Count != 1)
                {
                    // Dalamud has no tie-break for a name two repositories offer, and a repository that
                    // couldn't be read may have held it.
                    return offers.Count > 1 ? PluginInstallOutcome.OfferedTwice
                        : someUnavailable ? PluginInstallOutcome.RepositoryUnavailable
                        : PluginInstallOutcome.NotOffered;
                }

                repositoryUrl = offers[0].Url;
                entry = offers[0].Entries.First(candidate => candidate.InternalName == name);
            }

            // Dalamud's choice of build (PluginManager.cs L338-369), with testing builds only where the
            // restored settings turn testing on and opt this plugin in.
            var wantsTesting = doPluginTest && testingOptIns.Contains(name);
            if (entry.IsTestingExclusive && !wantsTesting)
            {
                return PluginInstallOutcome.TestingOnly;
            }

            var useTesting = entry.IsTestingExclusive
                || (wantsTesting && entry.TestingDalamudApiLevel == api && entry.TestingAssemblyVersion > entry.AssemblyVersion);
            var version = useTesting ? entry.TestingAssemblyVersion : entry.AssemblyVersion;
            if (version is null)
            {
                return PluginInstallOutcome.NotOffered;
            }

            // A left-out API level means the current one. Dalamud installs a higher level too, but only
            // updates an equal one, so XIV Vault keeps to equal.
            var apiLevel = (useTesting ? entry.TestingDalamudApiLevel ?? entry.DalamudApiLevel : entry.DalamudApiLevel) ?? api;
            if (apiLevel < api)
            {
                return PluginInstallOutcome.NotUpdated;
            }

            if (apiLevel > api || entry.MinimumDalamudVersion > dalamudVersion)
            {
                return PluginInstallOutcome.NeedsNewerDalamud;
            }

            if (!entry.AnyGameVersion)
            {
                return PluginInstallOutcome.NotForThisGameVersion;
            }

            if (bans.IsBanned(name, version))
            {
                return PluginInstallOutcome.Banned;
            }

            // Never DownloadLinkUpdate: Dalamud doesn't use it, and the official ones point at an archived repository.
            var link = useTesting ? entry.DownloadLinkTesting : entry.DownloadLinkInstall;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return PluginInstallOutcome.PackageRefused;
            }

            install = new PlannedInstall(plugin, entry, repositoryUrl, useTesting, version, link);
            return PluginInstallOutcome.Installed;
        }
    }
}
