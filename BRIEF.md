# Project brief: Skippy, a tiny instant-replay clip app for Windows 11

The app is called **Skippy**. Use that name everywhere: the project and executable (Skippy.exe), the window title, the tray icon tooltip, toast notifications, the autostart entry, the %AppData%\\Skippy folder for settings and logs, and the default save folder (Videos\\Skippy).

Build a small always-on tray app that works like NVIDIA ShadowPlay "Instant Replay" or Medal: it keeps the last few minutes of the screen in memory and saves them as an MP4 when I press a hotkey. It is NOT a recorder. It is for personal use by me and a few friends only (no code signing, no store, no installer polish needed).

**My PC:** Windows 11, 480 Hz monitor, GPU: 7900XTX (fill in; detect it yourself too). My friends' PCs have different monitors (e.g. 144 Hz) and GPUs.

Make all technical decisions yourself. Only ask me if you are truly blocked. Suggested stack (change it if you know better): C# on the latest .NET LTS, a tiny WPF or WinForms window + tray icon, Windows.Graphics.Capture for the monitor (borderless, no yellow capture border on Win11; fall back to DXGI Desktop Duplication if needed), hardware H.264 encoding (NVENC / AMF / QuickSync auto-detected, software fallback), WASAPI for audio (loopback + microphone), encoded frames kept in a RAM ring buffer, and MP4 muxing on save without re-encoding.

## The whole UI (keep it this simple, nothing else)

1. **Clip length:** 30 seconds / 1 minute / 2 minutes / 3 minutes.
2. **FPS:** 30 or 60 only. The monitor may run at 480 Hz, so capture at whatever rate is cheap and drop frames down to the chosen FPS before encoding.
3. **Audio:** a dropdown for the microphone (input device) and a dropdown for the headset / output device (its audio is captured with WASAPI loopback). Mic and game/system audio are mixed into one AAC track.
4. **Checkbox:** play a sound when a clip is saved.
5. **Monitor:** a dropdown that lists every connected monitor as "Screen 1", "Screen 2", "Screen 3", and so on, depending on how many monitors the user has (show the resolution next to the name so they are easy to tell apart). The user picks which one is captured. Default is the primary monitor. Refresh the list when monitors are plugged in or removed.

Small necessary extras (keep them minimal): one hotkey field (default Alt+F10, user can change it), a save folder (default Videos\\Skippy) with an "Open folder" button, and a tray menu with Settings / Pause / Resume / Open folder / Exit. Settings are stored as JSON in %AppData%.

## Behavior

* **Always captures the whole monitor** the user picked in the Monitor dropdown (primary by default), never a single window or game. It must keep working when a game is minimized, in borderless or fullscreen, or when I'm just browsing. Do not use per-window capture.
* Starts automatically when I log in to Windows (per-user, no admin) and starts buffering immediately. Single instance only. Lives in the system tray.
* Hotkey press saves the last N seconds (N = chosen clip length) to an MP4 named by timestamp, then plays the confirmation sound. Saving must not freeze or drop frames of the running buffer.
* Changing length, FPS or devices applies right away without restarting the app.
* Fixed sensible quality, no quality option in the UI (roughly 1080p60 at about 20 Mbps, scaled for other resolutions). A 3-minute buffer should stay around 450 MB of RAM or less.

## Errors and edge cases

* If the headset or microphone gets unplugged or the default device changes: the video buffer keeps running, the audio pipeline reconnects automatically when a device is available, and if it cannot recover I get a Windows **toast notification** with the error. Do not use a modal window, because it would steal focus from a fullscreen game. Handle device changes with IMMNotificationClient.
* Survive display changes, resolution changes, the lock screen and UAC prompts (black frames there are fine, crashes are not).
* If the selected monitor is unplugged or disabled, fall back to the primary monitor and show a toast notification. Don't silently record nothing.
* HDR is out of scope: assume SDR only, don't write any HDR or tone-mapping code.
* Log errors to a file in %AppData%.
* Audio and video must stay in sync over a 3-minute clip: mix by timestamps, not by arrival order, and handle clock drift between the mic and the loopback stream.

## Hard constraints

* **Anti-cheat safe:** no injection, no hooking, no game overlay, no drivers. Valorant (Vanguard), CS2 (VAC) and FACEIT must not be affected.
* Negligible performance impact on games; use the GPU hardware encoder.
* **Out of scope:** muting or filtering audio per app (no Discord or music muting), any cloud, any account, any sharing or upload feature.
* Ship as a normal `dotnet publish` build (self-contained, no obfuscation or packers, to avoid antivirus false positives).

## How to work

Build in stages. After each stage, build it, run what you can automatically, and tell me exactly what to test manually on my PC before moving on.

1. Video only: monitor capture, encode, RAM ring buffer, hotkey saves a playable MP4 (test with a game and with the game minimized).
2. Audio: mic + loopback mix, device selection, unplug and replug handling, sync check on a 3-minute clip.
3. Tray app, settings window, autostart, save sound, toast notifications.
4. Hardening: display changes, exclusive fullscreen, CPU/GPU/RAM measurement, and a short README with how to run and publish it.

