# Security policy

## Supported versions

Only the latest release of Capsule gets security fixes. Capsule checks for new versions once a day and asks you before installing.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Use GitHub's private reporting instead: open the repository's **Security** tab and choose
**Report a vulnerability**. Include what you found, the Capsule version, your Windows version,
and the steps to reproduce it. You'll get a reply within a week.

## What Capsule can and cannot do

Capsule is designed to have as little power as possible:

- **One network connection, and you can turn it off.** Once a day Capsule asks `api.github.com` for this project's
  latest release. Nothing about you or your PC is sent beyond what any web request carries (your IP address and a
  `Capsule/<version>` user agent). There is no telemetry, analytics or account. Turn it off under *General → Check for updates every day*.
- **Updates only with your permission.** A new version is downloaded only after you say yes, only over HTTPS, and only
  from this repository's release download address for a strict `vX.Y.Z` tag. The file must match the size GitHub
  announces and the SHA-256 listed in that release's `SHA256SUMS.txt`, or it is thrown away.
- **No administrator rights.** It runs as you (`asInvoker`), with no service, driver or scheduled task.
- **No keyboard logging.** The lock-key and language HUDs only read whether Caps Lock / Num Lock are on and which keyboard
  language is active. Capsule never sees what you type.
- **Camera and mic alerts read Windows' own privacy records**; Capsule never opens the camera or microphone.
- **Local files only.** Settings live in `%APPDATA%\Capsule\settings.json` and a small log in `%LOCALAPPDATA%\Capsule\`.
  Downloaded updates are kept in `%LOCALAPPDATA%\Capsule\updates` only until they're installed.
- **Native code is only loaded from System32**, never from the folder Capsule.exe sits in.
- **Start with Windows** uses your per-user `Run` registry entry, which you can turn off in Settings.
- **The experimental "hide the Windows volume pop-up" option** minimizes one small Explorer window (checked by class,
  size and Explorer's real path) while it's on, and puts it back when you turn the option off or quit.

### Known limit of the updater

The checksum file comes from the same GitHub release as the program, so it protects against broken or tampered
downloads, not against someone who has write access to this repository. Releases are protected by GitHub's tag
protection and immutable releases, and every release carries a signed
[build provenance attestation](https://docs.github.com/actions/security-guides/using-artifact-attestations-to-establish-provenance-for-builds)
you can check yourself (below). Install Capsule in a folder only your account can change, for example
`%LOCALAPPDATA%\Programs\Capsule`.

## Verifying a download

Every release is built by GitHub Actions from the tagged source, never on a personal machine. Each release includes
`SHA256SUMS.txt` and a signed build provenance attestation. To check that a file really came from this repository's
release workflow and tag:

```powershell
gh attestation verify .\Capsule-win-x64.exe --repo ahmedot/capsule-hud `
  --signer-workflow ahmedot/capsule-hud/.github/workflows/build.yml --source-ref refs/tags/vX.Y.Z
```

The executable is not code-signed yet, so Windows SmartScreen may warn the first time you run it.

## Security review

Before its first release, Capsule was audited with Cloudflare's open-source
[security-audit skill](https://github.com/cloudflare/security-audit-skill) (quick profile, source review).
It found no confirmed vulnerabilities. All four leads that needed more information were fixed anyway:

- device choices are no longer matched by name alone
- untrusted text is cleaned to one line
- album art is size- and dimension-checked and decoded off the UI thread
- release notes come from `CHANGELOG.md` instead of pull-request titles

The auto-updater and the system watchers added afterwards then had their own focused review.
