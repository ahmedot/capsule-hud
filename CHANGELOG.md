# Changelog

## 1.1.0

- **Switch the keyboard back to your usual language.** If you type in another language now and then, Capsule can switch the keyboard back to your usual one (Windows' default input language) once you haven't used the keyboard or mouse for 1, 2, 5, 10 or 30 minutes, and when you come back to a PC that was locked that long. Off by default; turn it on in Capsule's settings under *General › Keyboard*. It costs one check every 30 seconds while it's on and never sees what you type.
- **YouTube in the Now playing pill.** A YouTube video playing in your browser now shows a video icon, *YouTube* and *Playing* or *Paused*, instead of the video's title and thumbnail, when the video's tab is the one showing in its browser window. It also pops up only when you play or pause, not every time the next video starts. YouTube Music keeps the normal pill with the song and artist.
- **CPU and GPU alerts use Task Manager's figures.** The CPU figure is now the one Task Manager shows (it counts the extra speed a CPU reaches under load, so it often reads higher than before). The GPU figure is how busy your busiest graphics card is (video playback and copying don't count), so a PC with two graphics cards no longer adds them together.
- **Fewer missed alerts.** A busy CPU or GPU alert now shows when the *average* over 30 seconds reaches the limit, so a CPU that bounces around 90% (95, 88, 97...) still counts, while a short spike still doesn't.
- **The right app is named.** All of an app's processes are counted together (a browser runs dozens), protected programs such as games with anti-cheat are no longer skipped, and when no single app stands out the CPU and GPU pills say *Several apps are busy*. The memory alert also counts an app's processes together.

## 1.0.1

- **Home page shows whether Capsule starts with Windows.** A new chip says *Start with Windows on* or *Start with Windows off*, so it's easy to notice when it's off, including when it was switched off in Task Manager.
- **Turning on *General › Start with Windows* now always works.** It also undoes a switch-off made in Task Manager or in Windows Settings › Apps › Startup.
- Capsule's log now notes, each time it starts, whether it is set to start with Windows, to help track down start-up problems.

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
