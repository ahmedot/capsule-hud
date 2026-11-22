# Changelog

## 1.0.0

First release.

- **One-click install**: download `Capsule-Setup.exe`, open it, click **Install**. It installs just for you (no admin
  rights), adds Capsule to the Start menu and to Windows Settings › Apps, starts it with Windows (you can switch that
  off) and starts it straight away. *Try it without installing* runs it in place instead. Uninstall from Windows
  Settings › Apps.
- **Quick setup questions** while it installs: where it pops up, and others picked by a local look at the PC
  (headphones, games, keyboard languages, keyboard app, laptop battery), each with a recommended answer and a Skip
  button.
- **Capsule HUD**: one pill-shaped, animated pop-up. The icon appears first, the pill opens from the middle, then
  closes back the same way. It never takes focus and clicks pass through it.
- **Devices and sound**: headphones, game controllers (with battery when they report it), volume and mute, microphone
  mute, now playing with album art, USB drives, and a camera / microphone in-use privacy alert.
- **Keyboard, screen and network**: keyboard language switches, Caps Lock / Num Lock, laptop brightness,
  Wi-Fi / Ethernet / internet / VPN changes.
- **System health**: alerts when the CPU or GPU stays busy, the GPU runs hot or the CPU is slowed by heat, memory or
  disk space runs low, and for battery and charger changes (including headphones and controllers running low).
- **Auto-switching**: sound and calls move to headphones when they connect and back when they leave (calls can stay
  where they are). Your usual microphone is kept when Bluetooth headphones try to take it over.
- **Rename devices** for the HUD and Capsule's menu.
- **Make it yours**: position (one of six spots), monitor, size, dark / light / same-as-Windows, how long it stays,
  with a live preview. The HUD uses your Windows accent color (brightened if it's too dark to read), or switch that
  off to pick one of six colors.
- **One pop-up, not two**: hides the Windows volume / brightness pop-up (experimental, on by default), and the Caps
  Lock / Num Lock pop-up from Logi Options+, with a tip and a button to switch that off in Logi Options+ too.
  **Teach Capsule** handles most other apps: press the key, confirm the app it names, and its pop-up is hidden too.
- **One color rule** for every pop-up: accent for on or connected, gray for off, red only for real warnings.
- **Updates**: a daily check of GitHub releases (can be turned off); installs only when you agree, after verifying the
  download's size and SHA-256.
- **Light on your PC**: event-driven watchers, Windows Efficiency mode, GPU-cached HUD. Switching a pop-up off stops
  the code that watches for it, and pop-up checks pause while the PC is locked.
- Settings window with Home, Sound, HUD look, What it shows, Device names and General pages; light, dark or
  same-as-Windows.
