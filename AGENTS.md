# AGENTS.md

These are the working rules for agents in this repo. XIVault (Apache-2.0) backs up and restores the portable XIVLauncher / Dalamud configuration on Windows, with a CLI and an Avalonia desktop app on one .NET engine.

## Sources of truth

- `README.md`: what XIVault does, how to use it, and the safety model.
- `ROADMAP.md`: decisions already made, the milestones, and what is out of scope. Check it before proposing features, and respect those decisions unless the owner reopens them.
- Work from the next unchecked item in `ROADMAP.md`. Tick it off when it is done and record new decisions there, dated.
- The desktop UI implements the approved mockup. Don't redesign it.

## Architecture (hard rules)

- All behavior lives in `src/XIVault.Core`. `XIVault.Cli` and `XIVault.Desktop` only present it. Never duplicate backup, restore, discovery, validation or scheduling logic in a front end.
- File system, process, clock and environment access goes through the small services in Core (`IAppEnvironment`, `IProcessInspector`, `TimeProvider`, `ICommandRunner`), so tests can run in temp directories.
- Avalonia 12 ignores a `RenderTransform` set by a style unless the element already has one, so give it `RenderTransform="none"` (or set the transform inline).
- Check UI changes with `dotnet run --project tools/XIVault.Screenshots -- <folder>`, which renders every screen to PNG without touching the real profile or Task Scheduler.
- Desktop view models take interfaces only (`IDialogService`, `IShellService`, `IClipboardService`, `IFilePicker`). No Avalonia types in view models and no logic in code-behind.

## The safety contract

XIVault is trusted with configuration people spent years building. Never break these rules.

- **Allowlist only.** Back up and restore `pluginConfigs/`, `dalamudConfig.json`, `dalamudVfs.db` and optionally `dalamudUI.ini`. Never `installedPlugins/`, `runtime/`, `addon/`, logs, caches or binaries. Never archive the whole XIVLauncher folder.
- **Atomic backups.** Write `*.zip.tmp`, verify it, then rename. Retention runs only after a backup is verified and renamed.
- **Restore is guarded.** Validate the manifest, schema and every hash; refuse while XIVLauncher or FFXIV runs; always take a pre-restore snapshot; extract to a temp folder, never over the XIVLauncher folder; write only allowlisted paths; never delete files the backup doesn't contain.
- **Reject unsafe archives:** `..`, rooted or drive paths, entries outside `payload/`, files missing from the manifest, checksum mismatches.
- **Never execute anything from a backup.**
- **Never log or report configuration contents.** Logs and the diagnostic report hold paths, counts, versions and results only.
- The safety snapshot and hash verification are not settings. Don't add a way to turn them off.

## Commands

- `dotnet build` and `dotnet test` from the repo root. Tests use temp directories and never need a real XIVLauncher install.
- `dotnet format --verify-no-changes` is the CI style check. Run `dotnet format` before committing.
- `dotnet run --project src/XIVault.Cli -- <command>` runs the CLI; `dotnet run --project src/XIVault.Desktop` runs the app.
- `dotnet run scripts/make-icons.cs` rebuilds `docs/brand/xivault.ico` from `docs/brand/app-icon.svg`.

## Working style

- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `chore:`, `test:`, `ci:`, `build:`, `refactor:`), atomic, with a scope when it helps (`feat(restore): …`).
- **No AI attribution** in commits or PRs: no `Co-Authored-By` trailers, no "Generated with" lines, no session links.
- **Tests first for Core.** Every safety rule above has a test that proves it fails safely.
- **Docs:** short and concise. Prefer editing `ROADMAP.md` over new planning documents.
- **Code comments** explain why, not what: a non-obvious constraint, a workaround and its cause, or a line that keeps the safety contract. No boilerplate XML docs, no commented-out code.
- **UI copy:** calm, short, declarative. No exclamation marks or emoji. Explain things without assuming the user knows what `%APPDATA%` is.
