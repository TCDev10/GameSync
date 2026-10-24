# Security policy

## Reporting a vulnerability

If you discover a security issue in GameSync, please **do not** open a public GitHub issue with exploit details.

1. Open a [private security advisory](https://github.com/TCDev10/GameSync/security/advisories/new) on this repository, **or**
2. Contact the maintainer through GitHub (profile: [TCDev10](https://github.com/TCDev10)).

Include steps to reproduce, affected version, and impact if known. You should receive a response within a reasonable time.

## Scope

In scope:

- Unauthorized access to GitHub tokens or local save data
- Path traversal or arbitrary file write outside configured save directories
- Unsafe update download or install behavior
- Credential storage weaknesses on Windows

Out of scope:

- Social engineering of your own GitHub account
- Malware posing as a renamed `GameSync.exe` (verify downloads from official Releases)
- Issues in third-party dependencies without a practical GameSync attack path

## Documentation

See [docs/security.md](docs/security.md) for the full threat model, credential handling, and data locations.
