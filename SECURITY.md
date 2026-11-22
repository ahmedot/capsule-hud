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
- **Capsule's own Windows calls load system libraries only from System32.** A library resolver loads each of the
  eight Windows libraries Capsule uses by its full System32 path and refuses any other name, so a DLL placed next to
  Capsule.exe (for example in Downloads) is never used for them. (The .NET and WPF runtime files bundled inside
  Capsule.exe are unpacked and loaded by .NET itself.)
- **The installer stays inside your user account.** It copies the program to `%LOCALAPPDATA%\Programs\Capsule`, adds a
  Start menu shortcut, a *Settings › Apps* entry and, if *Start with Windows* is on (the default), a start-up entry,
  all for your account only, and nothing else. The installed copy is a plain
  byte copy of the file you ran, so it doesn't carry the download's "from the internet" mark; Windows already checked
  the file when you opened it. Uninstalling removes the program folder, the shortcut, the Apps entry, the start-up
  entry, the settings and the log.
- **Start with Windows** uses your per-user `Run` registry entry, which you can turn off in Capsule's settings
  (*General › Start with Windows*).
- **"Hide duplicate pop-ups from keyboard apps"** only acts for 1.5 seconds right after Capsule shows its own pop-up
  (Caps Lock or Num Lock; volume, mic and brightness only for apps you taught). It hides small windows (600 px or less
  at 100% scaling) of a known keyboard app's process, checked by the process's file name and the window's class, or
  small always-on-top windows of an app you taught with **Teach Capsule**, matched by its full program path and window
  class. It never changes another app's settings or files and never starts another program.
- **"Open Logi Options+"** is only offered for a Logi Options+ process in your own Windows session whose program is
  under *Program Files* (which only administrators can change), and that location is checked again when you click.
- **Teach Capsule** looks at the windows on your screen only while its window is open, and saves nothing until you
  confirm an app. It never offers a part of Windows itself.
- **The installer's setup questions** come from a quick local look at this PC: headphones, game controllers or a
  Steam install, keyboard languages, a laptop battery and the keyboard app. Nothing from it is saved or sent; only your
  answers become settings.
- **The installer refuses to run "as administrator"** (when UAC is on and you chose to elevate it), so it never
  installs into another account's profile or starts Capsule elevated. It also never replaces a newer installed version
  with an older one.
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

Capsule has been audited twice with Cloudflare's open-source
[security-audit skill](https://github.com/cloudflare/security-audit-skill) (quick profile, source review only).

**Audit 1**, before the first public push, covered the whole app as it was then. It found no confirmed
vulnerabilities, and all four leads that needed more information were fixed anyway:

- a device choice is ignored when more than one device has that name, so Capsule never guesses
- untrusted text is cleaned to one line
- album art is size- and dimension-checked and decoded off the UI thread
- release notes come from `CHANGELOG.md` instead of pull-request titles

**Audit 2**, before release 1.0.0, covered the installer and uninstaller, start with Windows, the updater, the release
workflow, Capsule's single-instance signals, the pop-up hiding and native library loading. It found no confirmed
vulnerabilities. Two leads that would need a sandboxed test to settle were fixed in code anyway:

- the "Open Logi Options+" button only trusts a Logi Options+ installed under Program Files and running in your own
  Windows session
- Capsule's Windows libraries are loaded by their full System32 path, never from the folder Capsule.exe runs in

Not yet audited, and first in line for the next audit: how the alert watchers handle Wi-Fi, drive and app names, the
installer's setup questions, and Teach Capsule.
