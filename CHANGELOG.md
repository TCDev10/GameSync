# Changelog

All notable changes to GameSync are documented here. Version numbers follow [Semantic Versioning](https://semver.org/).

## Unreleased

### Fixed
- In-app updates now target `TCDev10/GameSync` (legacy `TCDev` / `TCDev69` owners are remapped).
- Pre-launch sync no longer blocks game launch when GitHub fetch/pull fails; local saves are used instead.
- Resolving Git conflicts now applies to every conflicted path and completes the merge, ending the launch deadlock loop.

## [1.1.2] - 2026-08-20

### Fixed

- Reverted Native AOT publish — release builds use ReadyToRun again so the installed app starts reliably

## [1.1.1] - 2026-08-20

### Fixed

- Library home appeared blank (sidebar only) when Native AOT settings leaked into Debug builds and broke `ItemsSource` binding
- Save divergence dialog now automatically retries launch or completes sync after choosing local or remote saves, without reopening the app
- Launcher window closes on cancel instead of staying stuck in an error state

### Changed

- Native AOT is enabled only for Release publish profiles; `dotnet run` / Debug stay on CoreCLR

## [1.1.0] - 2026-08-19

### Added

- Save divergence dialog when local and remote saves differ: choose **Keep local saves** or **Use remote saves** before launch, manual sync, or after exit
- Immediate application of the chosen side (publish local to GitHub or restore remote locally) via `ResolveSaveDivergenceAsync`
- Shared conflict-resolution UI service used by game details and the launcher window
- Native AOT publish for Release builds (faster startup, no JIT at runtime)
- Source-generated JSON serialization (`GameSyncJsonContext`) for AOT-safe config and API payloads

### Changed

- Release installer is built with Native AOT (`PublishAot=true`, `PublishTrimmed=true`)
- Conflict dialog copy explains consequences for each choice (local vs GitHub)
- Post-exit sync checks for save divergence after integrating remote commits, not only Git index conflicts

### Fixed

- Launch and sync no longer silently overwrite divergent local saves with remote copies
- Launcher window surfaces the same divergence prompt as game details when pre-launch or post-exit sync conflicts

## [1.0.3] - 2026-08-12

### Added

- Global activity banner with progress for library load, sync, and launch operations
- Windows taskbar progress during long-running operations
- Remove game from library (commits the change and pushes to GitHub; existing save files in the repo are kept)
- Settings toggle to disable the automatic update check on startup
- Game details: Enter saves editable fields; improved drag-and-drop and paste for launch paths
- Steam import: fallback save-path suggestions when PCGamingWiki has none, with clearer messaging when a path is guessed

### Changed

- Library refresh loads sync statuses in parallel and shows a dedicated loading state
- Game details: repository save paths are assigned automatically; simplified save-location editor
- Launcher window uses a determinate progress bar instead of an indeterminate spinner
- Settings page uses a consistent full-width layout across all sections

### Fixed

- Legacy update feed owners (`TCDev` / `TCDev69`) are normalized to `TCDev10` for the official release repo
- Navigation selection glitches when opening Settings from the CLI

## [1.0.2] - 2026-08-12

### Added

- Working self-update: the release installer is downloaded, verified and installed unattended, and GameSync restarts itself — no manual download step
- Background update check a few seconds after startup, surfaced as a dismissable banner with an **Install update** action
- Download progress for the update in Settings
- `--check-update` and `--update` CLI commands (`--check-update` exits with `10` when a newer release exists)
- `GAMESYNC_UPDATE_ON_STARTUP=0` disables the startup check
- [docs/updates.md](docs/updates.md) documents the update flow, its verification steps and how to test it

### Security

- Update downloads are verified against the size and `sha256` digest GitHub publishes with the release asset and must carry an executable header; a payload failing any check is deleted instead of run. The installer is not code-signed, so this replaces signature validation.

### Fixed

- Installing an update no longer just opens a browser download; installers are started through the shell so Windows can present the UAC prompt the installer requires

## [1.0.1] - 2026-08-12

### Added

- Import games from an installed Steam library (registry + `libraryfolders.vdf` + `appmanifest_*.acf` discovery) with multi-select and per-game duplicate handling
- Headless CLI: `--help`, `--status`, `--settings`, `--sync`, `--game <id>` run in the console without opening the window
- Launch games through Steam (`steam://run/<appid>`), with an optional monitor process to detect when the session ends
- Drag and drop a `.exe` or `.lnk` shortcut onto the launch field; shortcuts are resolved to their target
- **Browse** button for every save location in Game details (previously only **Remove** was available)
- Git LFS is used automatically for save files larger than 50 MB under `saves/`
- New app logo, regenerated across app tiles, window icons, installer icon and docs (`tools/generate-brand-assets.ps1`)

### Changed

- Steam import only pre-fills save paths that actually exist on disk, and reports how many games still need a save path
- Game registration (`games.json` + `machine.json` + commit/push) moved into a shared `IGameRegistrationService`
- Releases now publish the `CHANGELOG.md` section for the tagged version as the release body

### Fixed

- In-app update checks pointed at the wrong GitHub owner, so no update was ever found

## [1.0.0] - 2026-08-12

### Added

- Native Windows desktop app (WinUI 3) for synchronizing game save files
- GitHub OAuth device flow sign-in (no client secret; token in Windows Credential Manager)
- Private GitHub repository as the sync backend (LibGit2Sharp — no separate Git install)
- Game library: add games, configure save paths and executables per PC
- Sync before launch and after exit; conflict resolution dialog for Git index conflicts
- Sync history with restore and local backups
- Inno Setup installer (`GameSync-Setup-x64.exe`) distributed via GitHub Releases
- In-app update check against GitHub Releases
- Local logging with secret redaction (`%LOCALAPPDATA%\GameSync\logs\`)

### Security

- Atomic file writes for save copies and backups
- Local save path policy blocks sensitive system folders
- HTTPS-only update downloads from GitHub hosts

[1.1.2]: https://github.com/TCDev10/GameSync/releases/tag/v1.1.2
[1.1.1]: https://github.com/TCDev10/GameSync/releases/tag/v1.1.1
[1.1.0]: https://github.com/TCDev10/GameSync/releases/tag/v1.1.0
[1.0.3]: https://github.com/TCDev10/GameSync/releases/tag/v1.0.3
[1.0.2]: https://github.com/TCDev10/GameSync/releases/tag/v1.0.2
[1.0.1]: https://github.com/TCDev10/GameSync/releases/tag/v1.0.1
[1.0.0]: https://github.com/TCDev10/GameSync/releases/tag/v1.0.0
