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

- **Network: GitHub only, and you can turn the daily check off.** Once a day Capsule asks `api.github.com` for this
  project's latest release, and only after you agree to an update does it download that release from github.com.
  Nothing about you or your PC is sent beyond what any web request carries (your IP address and a `Capsule/<version>`
  user agent). There is no telemetry, analytics or account. Turn the daily check off in Capsule's settings under
  *General › Check for updates every day*.
- **Updates only with your permission.** A new version is downloaded only after you say yes, only over HTTPS, and only
  from this repository's release download address for a strict `vX.Y.Z` tag. The file must match the size GitHub
  announces and the SHA-256 listed in that release's `SHA256SUMS.txt`, or it is never installed (the file is deleted at
  the next update attempt).
- **No administrator rights.** It runs as you (`asInvoker`), with no service, driver or scheduled task.
- **No keyboard logging.** The lock-key and language HUDs only read whether Caps Lock / Num Lock are on and which keyboard
  language is active. Capsule never sees what you type.
- **Camera and mic alerts read Windows' own privacy records**; Capsule never opens the camera or microphone.
- **Local files only.** Settings live in `%APPDATA%\Capsule\settings.json` and a small log in `%LOCALAPPDATA%\Capsule\`.
  Downloaded updates are kept in `%LOCALAPPDATA%\Capsule\updates` until the next update or until Capsule is uninstalled.
- **Capsule's own Windows calls load system libraries only from System32**, never from the folder Capsule.exe sits
  in. (The .NET and WPF runtime files bundled inside Capsule.exe are unpacked and loaded by .NET itself.)
- **The installer stays inside your user account.** It copies the program to `%LOCALAPPDATA%\Programs\Capsule`, adds a
  Start menu shortcut, a *Settings › Apps* entry and, if *Start with Windows* is on (the default), a start-up entry,
  all for your account only, and nothing else. The installed copy is a plain
  byte copy of the file you ran, so it doesn't carry the download's "from the internet" mark; Windows already checked
  the file when you opened it. Uninstalling removes the program folder, the shortcut, the Apps entry, the start-up
  entry, the settings and the log.
- **Start with Windows** uses your per-user `Run` registry entry, which you can turn off in Capsule's settings
  (*General › Start with Windows*).
- **"Hide duplicate pop-ups from keyboard apps"** only acts for 1.5 seconds right after Caps Lock or Num Lock changes.
  It hides small windows of a known keyboard app's process (checked by the process's file name and the window's class
  and size), and never changes that app's settings or files.
- **The experimental "Hide the Windows volume pop-up" option** (on by default) minimizes one small Explorer window
  (checked by class, size and Explorer's real path) while it's on, and puts it back when you turn the option off or
  quit.

### Known limit of the updater

The checksum file comes from the same GitHub release as the program, so it protects against broken or tampered
downloads, not against someone who has write access to this repository. Releases are protected by GitHub's tag
protection and immutable releases, and every release carries a signed
[build provenance attestation](https://docs.github.com/actions/security-guides/using-artifact-attestations-to-establish-provenance-for-builds)
you can check yourself (below). Install Capsule in a folder only your account can change, for example
`%LOCALAPPDATA%\Programs\Capsule`.

## Verifying a download

Every release is built by GitHub Actions from the tagged source, never on a personal machine. Each release includes
`SHA256SUMS.txt` and a signed build provenance attestation.

**Quick check:** in PowerShell, run this in the folder you downloaded to, and compare the result with the line for
`Capsule-Setup.exe` in that release's `SHA256SUMS.txt`:

```powershell
Get-FileHash .\Capsule-Setup.exe -Algorithm SHA256
```

**Full check** that the file was built by this repository's release workflow from that tag (needs the
[GitHub CLI](https://cli.github.com)):

```powershell
gh attestation verify .\Capsule-Setup.exe --repo ahmedot/capsule-hud `
  --signer-workflow ahmedot/capsule-hud/.github/workflows/build.yml --source-ref refs/tags/vX.Y.Z
```

The executable is not code-signed yet, so Windows SmartScreen may warn the first time you run it.

## Security review

Before its first release, Capsule was audited with Cloudflare's open-source
[security-audit skill](https://github.com/cloudflare/security-audit-skill) (quick profile, source review).
It found no confirmed vulnerabilities. All four leads that needed more information were fixed anyway:

- a device choice is ignored when more than one device has that name, so Capsule never guesses
- untrusted text is cleaned to one line
- album art is size- and dimension-checked and decoded off the UI thread
- release notes come from `CHANGELOG.md` instead of pull-request titles

The auto-updater and the system watchers added afterwards then had their own focused review.
