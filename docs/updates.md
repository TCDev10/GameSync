# Updates

GameSync updates itself from the GitHub Releases of `TCDev10/GameSync`. The app downloads the
Inno Setup installer published with the release, verifies it, and runs it unattended.

## Flow

1. **Check** — `GET https://api.github.com/repos/{owner}/{repo}/releases/latest`. The tag (`v1.2.3`,
   pre-release suffixes ignored) is compared against the version baked into the build
   (`AppVersion.Semantic`). The release must publish an asset named `GameSync-Setup*.exe`.
2. **Download** — the asset is streamed to `%LOCALAPPDATA%\GameSync\updates\` as a `.part` file with
   progress reporting. Only `github.com` and `*.githubusercontent.com` hosts over HTTPS are accepted.
3. **Verify** — the download must match the size and the `sha256` digest that GitHub publishes with
   the asset, and must start with the `MZ` executable signature. Anything else is deleted and never
   executed. The installer is not code-signed, so this digest is the integrity guarantee.
4. **Install** — the verified installer is started through ShellExecute (so Windows can show the UAC
   prompt) with `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`.
   GameSync then exits so Inno Setup can replace program files, and Inno Setup reopens it.

`%LOCALAPPDATA%\GameSync\` (machine config, logs, backups, clones) is outside the install directory
and is never touched. A failed or rejected update leaves the installed app untouched.

## Entry points

| Where | Behavior |
|-------|----------|
| Startup banner | A background check runs a few seconds after the window opens and shows an InfoBar when a newer release exists. Nothing downloads until you accept. |
| Settings → Automatic updates | **Check** reports the status; **Update** downloads, verifies and installs with a progress bar. |
| `GameSync.exe --check-update` | Prints installed/latest version, installer URL, size and SHA-256. Exit code `0` up to date, `10` update available, `1` check failed. |
| `GameSync.exe --update` | Downloads, verifies and starts the installer from the console. |

The startup check can be disabled in **Settings → Automatic updates → Check on startup**, with
`GAMESYNC_UPDATE_ON_STARTUP=0`, or by repointing the feed with
`GAMESYNC_UPDATE_OWNER` / `GAMESYNC_UPDATE_REPO`.

## Testing

`GitHubReleaseAppUpdateServiceTests` covers detection, digest/size mismatches, non-executable
payloads and untrusted hosts with a stubbed HTTP handler — a rejected payload must never reach the
installer launcher.

`GitHubReleaseAppUpdateLiveTests` runs the same production code against the real Releases feed:
live API call, download through GitHub's redirect, and SHA-256 verification. It is opt-in because it
needs network and downloads ~75 MB, and it never executes the installer:

```powershell
$env:GAMESYNC_LIVE_UPDATE_TEST = '1'
dotnet test tests/GameSync.Infrastructure.Tests -p:Version=0.9.0 `
  --filter FullyQualifiedName~GitHubReleaseAppUpdateLiveTests
```

The `-p:Version=0.9.0` override makes the published release look newer than the build under test.
The same trick verifies the real app end to end without installing anything:

```powershell
dotnet build src/GameSync.App/GameSync.App.csproj -p:Version=0.9.0
.\src\GameSync.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\GameSync.exe --check-update
```
