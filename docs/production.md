# Production setup & release guide

How to configure GameSync and publish a usable installer for real users.

**Policy for this product:**

- **No code signing** — ship unsigned `GameSync-Setup-x64.exe`. Windows SmartScreen may warn; users choose “More info → Run anyway”.
- **No Git for end users** — GameSync embeds Git via **LibGit2Sharp**. Users do **not** install `git.exe`.
- Distribution is **Inno Setup** (`GameSync-Setup-x64.exe`), not MSIX / MSI.

---

## 1. Create the GitHub OAuth App

GameSync uses **OAuth Device Flow** (no browser redirect into the app).

1. Open [GitHub → Settings → Developer settings → OAuth Apps → New OAuth App](https://github.com/settings/developers).
2. Fill the form:

| Field | What to put |
|-------|-------------|
| **Application name** | `GameSync` (or any display name) |
| **Homepage URL** | Repo or project page, e.g. `https://github.com/YOUR_USER/GameSync` |
| **Authorization callback URL** | Required by GitHub’s form, **unused by Device Flow**. Use a placeholder, e.g. `http://127.0.0.1/` or the same Homepage URL. |
| **Enable Device Flow** | **Must be checked** (checkbox on the OAuth App settings page after create, or while editing). |

3. After create, copy the **Client ID** (public). You do **not** need a Client Secret for Device Flow.

### Why callback URL is required but unused

Classic “Authorization code” apps redirect the browser to the callback URL. GameSync never does that: the user opens `github.com/login/device`, enters a code, and GameSync polls GitHub for the token. GitHub still forces the callback field when registering the app — any valid `http(s)://…` URL is fine.

Details: [docs/github-authentication.md](github-authentication.md).

---

## 2. One-time build machine setup

| Tool | Why |
|------|-----|
| Windows 10/11 + [.NET 10 SDK](https://dotnet.microsoft.com/download) | Build / publish |
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | Compile `GameSync-Setup-x64.exe` (`ISCC.exe`) |
| GitHub repo with Actions | CI builds installer on version tags |

End users need **only** the Setup.exe (Windows 10 19041+ / Windows 11). No .NET SDK, no Windows App SDK install, no Git.

---

## 3. Runtime configuration (OAuth + updates)

Defaults are baked into [`GameSyncOptions`](../src/GameSync.Core/Options/GameSyncOptions.cs):

| Setting | Default |
|---------|---------|
| `GitHubClientId` | `Ov23lifRWe1kBZkxMufT` |
| `UpdateReleasesOwner` | `TCDev10` |
| `UpdateReleasesRepo` | `GameSync` |

Optional overrides (development / special builds):

| Variable | Purpose |
|----------|---------|
| `GAMESYNC_GITHUB_CLIENT_ID` | Replace OAuth Client ID |
| `GAMESYNC_UPDATE_OWNER` | Replace Releases owner |
| `GAMESYNC_UPDATE_REPO` | Replace Releases repo |
| `GAMESYNC_UPDATE_ON_STARTUP` | Set to `0` to suppress the background update check on startup |

```powershell
dotnet run --project src/GameSync.App/GameSync.App.csproj -p:Platform=x64
```

The Client ID is public. Never commit a Client Secret (not used for Device Flow).

---

## 4. Versioning

Single source of truth: [`Directory.Build.props`](../Directory.Build.props)

```xml
<Version>1.0.0</Version>
```

| Surface | Value |
|---------|--------|
| Assemblies / About | `1.0.0` |
| Inno Setup | same, via `build-release.ps1 -Version` |
| GitHub Release tag | `v1.0.0` |

Bump `Version`, commit, then tag `vMAJOR.MINOR.PATCH`.

---

## 5. Build the installer (local)

```powershell
# From repo root
dotnet test GameSync.sln -c Release -p:Platform=x64
.\installer\build-release.ps1
# or pin version:
.\installer\build-release.ps1 -Version 1.0.3
```

Output: `artifacts\GameSync-Setup-x64.exe`

More detail: [installer/README.md](../installer/README.md).

**Code signing:** intentionally **not** used. Do not run `signtool`. SmartScreen warnings on first run are expected.

---

## 6. Publish to users

### Automatic (recommended)

```powershell
git add -A
git commit -m "Release 1.0.0"
git tag v1.0.0
git push origin HEAD
git push origin v1.0.0
```

[`.github/workflows/release.yml`](../.github/workflows/release.yml) builds unpackaged Release + Inno Setup and attaches `GameSync-Setup-x64.exe` to the GitHub Release.

### Manual

Upload your local `artifacts\GameSync-Setup-x64.exe` to a GitHub Release named/tagged `v1.0.0`.

### What users do

1. Download `GameSync-Setup-x64.exe`.
2. If SmartScreen appears: **More info → Run anyway**.
3. Install (admin / Program Files).
4. Sign in with GitHub (device code), pick/create their private save repo, add games.

User data lives in `%LOCALAPPDATA%\GameSync\` and survives update/uninstall. Credentials stay in Windows Credential Manager.

---

## 7. Git is embedded (no git.exe)

| Component | Role |
|-----------|------|
| **LibGit2Sharp** | Clone / fetch / pull / commit / push inside the app |
| **HTTPS + OAuth token** | Auth to GitHub (Credential Manager) |

Users never install Git for Windows. Developers may use Git only to work on this source repo.

---

## 8. In-app updates

With `GAMESYNC_UPDATE_OWNER` / `GAMESYNC_UPDATE_REPO` (or baked defaults) set:

1. Settings checks the latest GitHub Release tag.
2. If newer than `AppVersion`, opens the HTTPS download for `GameSync-Setup-*.exe`.
3. User runs the new installer; LocalAppData is preserved.

---

## 9. Pre-ship checklist

1. OAuth App created, **Device Flow** enabled (Client ID already defaulted in `GameSyncOptions`).
2. Callback URL filled with placeholder (`http://127.0.0.1/` is fine).
3. First Release asset named `GameSync-Setup-x64.exe` on `TCDev10/GameSync`.
4. `Version` in `Directory.Build.props` matches tag `vX.Y.Z`.
5. Local test: install Setup → login → sync → shortcuts → About version.
6. Uninstall: Program Files gone; `%LOCALAPPDATA%\GameSync\` still there.
7. Manual QA matrix in [development.md](development.md) as needed.

---

## 10. Related docs

| Doc | Topic |
|-----|--------|
| [github-authentication.md](github-authentication.md) | Device flow details |
| [release.md](release.md) | Versioning / CI notes |
| [installer/README.md](../installer/README.md) | Inno packaging |
| [security.md](security.md) | Threat model |
| [configuration.md](configuration.md) | Local vs shared config |
