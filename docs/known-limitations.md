# Known limitations

GameSync v1.0.0 is a desktop tool for technical users who manage their own private GitHub repository. The following limitations are intentional or planned for future releases.

## Sync and conflicts

- **Save-level divergence** — If local saves differ from the remote copy before launch, GameSync refuses to overwrite silently. You must resolve Git index conflicts via the in-app Conflict dialog; there is no dedicated wizard yet for save-folder divergence detected outside Git conflicts.
- **Multiple conflicts** — When several files conflict at once, the UI resolves the first conflict only; repeat until clean.
- **Corrupt clones** — A damaged local Git clone is not automatically repaired. Delete `%LOCALAPPDATA%\GameSync\repos\` and reconnect if sync fails persistently.

## Distribution

- **Unsigned installer** — `GameSync-Setup-x64.exe` is not code-signed. Windows SmartScreen may warn on first run; choose **More info → Run anyway**.
- **Updates** — In-app updates download the Setup.exe from GitHub Releases (`TCDev10/GameSync` by default), verify its SHA-256 and install it unattended. Requires network access, and Windows shows a UAC prompt because the installer writes to Program Files. A release without a `GameSync-Setup*.exe` asset cannot be installed automatically.

## Platform

- **Windows only** — x64 installer; Windows 10 (build 19041+) or Windows 11.
- **Single machine** — Sync is serialized per PC. Two GameSync windows on the same machine share one sync lock; gameplay is not locked across different PCs.
- **Game executables** — You choose which `.exe` to launch. That is arbitrary code execution by design.

## Steam import

- **Windows only** — Steam library discovery reads the Windows registry (`HKCU\Software\Valve\Steam`) and local `libraryfolders.vdf` / `appmanifest_*.acf` files. Steam must be installed.
- **Installed games only** — Only games currently installed on this PC appear in the import list. Owned-but-uninstalled titles are not visible.
- **Save paths are heuristic** — Steam manifests do not expose save file locations. Suggested paths come from common conventions and may need manual adjustment in Game Details after import.
- **Tools and redistributables** — Steamworks Common Redistributables, dedicated servers, and similar non-game entries may appear in the list. Deselect them before importing.
- **Metadata fetch** — Cover art and save-path suggestions require a network request to the Steam Store API. Failures are non-blocking; the game is still imported without a cover.

## Privacy

- **No telemetry** — GameSync does not send crash reports or usage data to a vendor. Logs stay in `%LOCALAPPDATA%\GameSync\logs\`.

## Related

- [security.md](security.md) — threat model and credential handling
- [sync-workflow.md](sync-workflow.md) — launch and sync lifecycle
