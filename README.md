# Skippy

A tiny always-on instant-replay app for Windows 11. It keeps the last 30 s – 3 min of your screen and
sound in RAM and writes an MP4 when you press a hotkey (default **Alt+F10**). It is not a recorder: nothing
touches the disk until you save.

- Whole-monitor capture (Windows.Graphics.Capture), works with borderless, exclusive fullscreen, minimized games and the desktop
- Hardware H.264 on the GPU (AMD AMF / NVIDIA NVENC / Intel Quick Sync, auto-detected; software fallback)
- Microphone + headset/game audio mixed into one AAC track, kept in sync by timestamps (drift-corrected)
- No injection, no hooks, no overlay, no driver: safe with Vanguard (Valorant), VAC (CS2) and FACEIT

## Using it

1. Start `Skippy.exe` once. It goes to the system tray and starts buffering immediately. From then on it
   starts by itself when you log in to Windows.
2. Press **Alt+F10** to save the last N seconds. You hear a short chime and get a notification; the clip
   lands in `Videos\Skippy` as `Skippy_YYYY-MM-DD_HH-MM-SS.mp4`.
3. Click the tray icon (or start `Skippy.exe` again) for the settings window. Everything applies instantly:
   - **Clip length** 30 s / 1 / 2 / 3 min, **frame rate** 30 / 60 fps
   - **Microphone** and **headset / game audio** device (or Windows default), with live level meters
   - **Sound** when a clip is saved
   - **Screen**: click the monitor to record (Screen 1, 2, …)
   - **Shortcut**: click the keycaps and press a new combo
   - **Save folder**, and a link to the last clip
   - Click the **Recording** pill in the title bar to pause/resume
   - Live view while the window is open: buffer dial, resolution / encoder / audio / RAM tiles, LED audio
     meters, a small preview of the screen being recorded, and the last clip with its thumbnail
4. Tray menu: Settings / Pause / Resume / Trim a clip / Open folder / Exit.

## Trimming a clip

Click **✂ Trim** on the last-clip card (or **Trim another clip…**, or *Trim a clip* in the tray menu) to cut a
piece out of a clip, e.g. from 0:34 to 0:39. Drag the purple start and pink end handles on the filmstrip, type
exact times, or use the keys: **Space** play, **← →** one frame, **Shift+← →** one second, **I** / **O** set
start / end at the current frame, **L** loop the cut, **Ctrl+S** save.

**Save cut** writes a new file to `Videos\Skippy\EditedSkippy` (inside whatever save folder you chose), named
after the original plus the range, e.g. `Skippy_2026-11-03_07-12-23_0m34.0s-0m39.0s.mp4`. The original is never
changed. Cuts are exact to the frame: the kept part is decoded and encoded again on the GPU (about a second for
a 5-second cut) at a slightly higher bitrate than the original, so there is no visible quality loss.

Video is 1080p (the short side is capped at 1080 px; e.g. a 1440p monitor is scaled to 1920×1080),
about 18 Mbps at 60 fps, 11 Mbps at 30 fps. Audio is AAC 192 kbps.

## Giving it to friends

Zip the whole `publish` folder and send it. They unzip it somewhere permanent (for example
`C:\Games\Skippy` — not the Downloads folder, since autostart points to wherever it was started from) and
run `Skippy.exe`. Nothing else to install: the .NET runtime is included.

Because the app is not code-signed, Windows may warn the first time:

- **SmartScreen** ("Windows protected your PC"): click *More info* → *Run anyway*. Only needed once.
- **Smart App Control** (Windows 11, *Windows Security → App & browser control*): if it is **On**, Windows
  decides per file, by cloud reputation, whether unsigned programs may run. During development it allowed
  the release build but blocked one debug build. If Skippy won't start and the Code Integrity log mentions
  `Skippy.dll`, the only options are signing the app or turning Smart App Control off (which Windows can't
  turn back on without a reset — so it's the user's call, Skippy doesn't need it off otherwise).

### Avoiding the warnings without paying for a certificate

- **SignPath Foundation** gives free code signing to open-source projects (this repo is public). Apply at
  signpath.org; once approved, a GitHub Action signs each release with a certificate Windows trusts, which
  satisfies Smart App Control and quickly builds SmartScreen reputation. This is the real fix.
- **Microsoft file submission** (free, per release): submit the new `Skippy.exe`/`Skippy.dll` at
  microsoft.com/wdsi/filesubmission as a software developer reporting an incorrect block. It can clear
  SmartScreen / Smart App Control verdicts for those exact files, but has to be repeated for every version.
- Self-signed certificates do **not** help: Windows only trusts certificates from its trusted root program.

## Updates

The **Check for updates** button (top of the settings window) reads the latest release of this GitHub repo.
If there is a newer version it turns into **Update to vX.Y.Z**; clicking that downloads the zip, verifies its
SHA-256 checksum, makes sure Windows will run the new build (if Smart App Control blocks it you simply stay
on the current version), then swaps the files and restarts Skippy. Settings and clips are untouched. Skippy
never contacts the internet unless you press the button.

To publish a new version: bump `<Version>` in `src\Skippy\Skippy.csproj`, commit, then

```bash
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Notes "What changed"
```

It builds, zips to `dist\`, tags, pushes and creates the GitHub release that everyone's button picks up.

## Building

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```bash
dotnet publish src/Skippy -c Release -o publish
```

This produces a normal self-contained build in `publish\` (no single-file packing, no trimming, no
obfuscation — all of which tend to upset antivirus). Run `publish\Skippy.exe`.

## Files

| What | Where |
|---|---|
| Clips | `%USERPROFILE%\Videos\Skippy` (changeable) |
| Settings | `%AppData%\Skippy\settings.json` |
| Log (errors, device changes, saves) | `%AppData%\Skippy\skippy.log` |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `Skippy` |

## Uninstalling

Exit Skippy from the tray, then:

```bash
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Skippy /f
```

```bash
reg delete "HKCU\Software\Classes\AppUserModelId\Skippy.InstantReplay" /f
```

and delete the app folder and `%AppData%\Skippy`. Your clips stay in `Videos\Skippy`.

## What it costs (measured)

On a Ryzen + RX 7900 XTX, 2560×1440 @ 480 Hz, 1080p60, 3-minute buffer:

| | |
|---|---|
| CPU | ~0.8 % of all cores |
| GPU 3D engine (shared with games) | ~1.4 % (copy + colour conversion) |
| GPU video encoder (separate chip) | ~16 % |
| VRAM | ~170 MB |
| RAM | ~345 MB total with a 3-minute buffer of normal desktop use, flat over a 30-minute soak test (no leak). The video buffer is ~130 MB of that; with constant fast action it grows to at most ~405 MB (18 Mbps × 180 s), so expect ~600 MB worst case with a 3-minute clip length, ~300 MB with 1 minute |
| Saving a clip | 2-minute clip in ~50 ms; capture keeps running, no dropped frames |
| Dropped frames | 11 of 108,000 over 30 minutes (0.01 %) |

Measure it yourself while Skippy runs: `powershell -ExecutionPolicy Bypass -File tools\measure.ps1`.
To check game impact, compare your in-game FPS (or CapFrameX / PresentMon) with Skippy running vs. paused
from the tray.

## Behaviour on problems

- **Headset or mic unplugged / default device changed**: video keeps buffering, audio reconnects by itself
  (falling back to the Windows default meanwhile). If a source stays unavailable for 5 s you get a
  notification, and another one when it is back.
- **Monitor unplugged or disabled**: recording moves to the primary monitor with a notification, and back
  when your chosen screen returns.
- **Resolution changes, exclusive fullscreen, lock screen, UAC prompts**: capture continues (black or frozen
  frames on secure screens are expected). The buffer is kept.
- **GPU driver reset / capture stalls**: a watchdog rebuilds the pipeline within ~6 s.
- **Sleep / hibernate**: capture is rebuilt cleanly after resume.
- **Crash**: logged, a notification is shown, and Skippy restarts itself.
- Notifications never steal focus; during fullscreen games Windows holds them in the notification centre.

## How it works

`ScreenCapture` (WGC) copies each new desktop frame into a texture. A high-resolution timer samples it at
exactly 30/60 fps, the GPU video processor scales/converts it to NV12, and the hardware H.264 encoder MFT
encodes it with a 1-second keyframe interval. Encoded frames go into `PacketBuffer`, a time-bounded RAM ring
of reusable 4 MB chunks. WASAPI captures the mic and the output device (loopback); `AudioMixer` places every
packet on the shared QPC clock and trims single samples to absorb device clock drift, then the Windows AAC
encoder compresses the mix. Saving snapshots both buffers (no copying, no pause), starts at the last keyframe
before "now − N s" and writes an MP4 with a small built-in muxer — no re-encoding.

## Developer self-tests

Run from a terminal; reports go to `%AppData%\Skippy\selftest.txt`.

| Command | Checks |
|---|---|
| `Skippy.exe --selftest 12` | buffer 12 s, save, decode every frame |
| `Skippy.exe --selftest 8 --software` | same with the software encoder |
| `Skippy.exe --selftest-sync 185` | 3-minute A/V sync: flashes a square + beeps every 10 s, measures offset and drift |
| `Skippy.exe --selftest-fullscreen` | real exclusive-fullscreen swap chain with solid colours, checks they were recorded |
| `Skippy.exe --soak 1800` | 30 minutes with a 3-minute buffer, logs memory/handles/CPU each minute |
| `Skippy.exe --selftest-trim [clip.mp4]` | trims a numbered test video at several ranges and checks the exact frames and audio length (add `--software` for the CPU path) |
| `Skippy.exe --preview-trim clip.mp4` | opens just the trim window, next to a running Skippy |
