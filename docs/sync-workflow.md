# Sync and launch workflow

Pre/post save synchronization is owned by `ISyncService` (`SyncService`). The full launch lifecycle is owned by `IGameLauncher` / `GameLauncher` (also exposed as `IGameLaunchWorkflow`). Neither belongs in UI code-behind.

## Sync engine components

| Service | Responsibility |
|---------|----------------|
| `IGitService` / `GitService` | Embedded Git (LibGit2Sharp) |
| `IRepositoryService` / `RepositoryService` | Deterministic local clones |
| `ISaveService` / `SaveService` | Local ↔ repository save copy |
| `IBackupService` / `BackupService` | Timestamped backups + retention |
| `ISyncService` / `SyncService` | `SyncBeforeGameLaunch` / `SyncAfterGameExit` |
| `IGameLauncher` / `GameLauncher` | Validate → sync before → start → wait → sync after |
| `IProcessLauncher` / `WindowsProcessLauncher` | Process start + event-driven exit wait |
| `IShortcutService` / `WindowsShortcutService` | Desktop / Start Menu shortcuts to GameSync |

## CLI entry points

| Invocation | Behavior |
|------------|----------|
| `GameSync.exe` | Open dashboard |
| `GameSync.exe --game <id>` | Lightweight launcher window + full launch lifecycle |
| `GameSync.exe --sync` | Synchronize all games (console) |
| `GameSync.exe --sync <id>` | Synchronize one game (console) |
| `GameSync.exe --status` | Print repository and per-game status (console) |
| `GameSync.exe --settings` | Print machine/repository settings (console) |
| `GameSync.exe --check-update` | Report the newest release and its installer digest (console) |
| `GameSync.exe --update` | Download, verify and install the newest release (console) |
| `GameSync.exe --help` | Print help (console) |

Parser: `AppCommandParser` in Core (supports `--`, `-`, `/`). All commands except a bare `GameSync.exe` invocation run headless in the console (exit code `0` on success, non-zero on failure; `--check-update` returns `10` when an update is available).

## Launch lifecycle (`--game`)

1. Resolve shared game (`games.json`) and local launch config (`machine.json`).
2. Validate launch target: local `.exe` path or supported protocol URI (`steam://`).
3. Report phases: Preparing → Checking repository → Downloading saves → Restoring saves.
4. `SyncBeforeGameLaunchAsync` — **on failure, do not launch**.
5. Start via `IProcessLauncher` (`.exe`) or `IProtocolLauncher` (`steam://`).
6. Wait for exit: direct process wait, monitor process name (`monitorExecutable`), or manual confirmation for Steam without a monitor exe.
7. On exit: Game closed → Saving changes → Uploading changes → `SyncAfterGameExitAsync`.
8. Completed or Error.

`GameExecutableNotFound` includes **game id** and **configured path**.

Cancellation / closing GameSync while the game runs: wait is cancelled, the game process is **not** killed, post-exit sync is skipped, result is marked cancelled.

## Launcher window

`--game` does **not** open the dashboard. `LauncherWindow` + `LauncherViewModel` bind to `LaunchPhase` / status text:

Preparing, Checking repository, Downloading saves, Restoring saves, Launching game, Game running, Game closed, Saving changes, Uploading changes, Completed, Error.

## Single instance

`Program.Main` uses Windows App SDK `AppInstance.FindOrRegisterForKey("GameSync.Main")`. A second `GameSync.exe --game …` activation is redirected to the existing process, which opens another launcher window via `AppActivationService`.

## Shortcuts

Shortcuts target **GameSync.exe** with arguments `--game <game-id>` (never the game executable). Changing the executable path in `machine.json` does not break shortcuts.

Locations:

- Desktop: `%USERPROFILE%\Desktop\<Title>.lnk`
- Start Menu: `%APPDATA%\Microsoft\Windows\Start Menu\Programs\GameSync\<Title>.lnk`

Display names are sanitized for invalid Windows filename characters.

## SyncBeforeGameLaunch / SyncAfterGameExit

Unchanged from the sync engine (fetch/pull/restore and detect/copy/commit/push). See earlier sections in repo docs for conflict and backup rules.

## Current limitations

- Pre-launch sync soft-fails when GitHub is unreachable: the game launches with local saves and sync retries after exit.
- Git conflict resolution applies the chosen side to **all** conflicted paths and completes the merge commit (resolving only the first file used to leave the repo stuck).
- Save-divergence still requires an explicit Local/Remote choice before launch (never silent overwrite).
- Shortcut targets resolve via `Environment.ProcessPath` (installed or published GameSync.exe).
