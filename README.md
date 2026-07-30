# MouseUtil

**MouseUtil** is a lightweight Windows utility that keeps your mouse "active" by clicking or
jiggling/moving in place, so your PC doesn't go idle, lock, or sleep during long calls,
downloads, or AFK sessions. It runs natively on Windows 10/11 (WinUI 3 / .NET 8) with a clean,
minimal interface.

### Download

To download MouseUtil, head over to the [Releases](https://github.com/MouseUtil/MouseUtil/releases)
page and grab the MouseUtilSetup zip.

### Modes

- **Auto click** — clicks at wherever the cursor currently is without moving it. Great for repetitive
clicking tasks, keeping certain applications active, or automating simple interactions.

- **Spin mode** — sweeps the cursor around a tiny circle and returns it to the exact starting pixel,
without clicking. Useful for keeping a machine from going idle/locking/sleeping.

### Features

- 🖱️ **Two automation modes** — Auto click, which sends a real left-click at a set interval, or
Spin mode, which sweeps the cursor in a tiny circle and returns it to its exact starting pixel.
- ⏱️ **Configurable interval** — set the delay between actions in minutes and seconds.
- ✋ **Pause on manual movement** (Spin mode) — if you touch the mouse yourself, MouseUtil
automatically pauses and resumes on a fresh countdown once you stop, so it never fights you
for control.
- 🛑 **Auto-stop conditions** — stop the run automatically after a set number of clicks/spins,
or at a specific date and time.
- ⌨️ **Global hotkey** — start or stop automation from anywhere with a single keypress (F6 by
default), even while another app is focused.
- 📊 **Live status and progress** — a real-time countdown ("Clicking in 12s"), action counter,
and an optional progress bar mirrored onto the taskbar icon, all of which are optional.
- 📥 **System tray support** — optionally close the window to the tray instead of exiting, so
automation keeps running in the background.
- 🎨 **Fluent design** with light/dark/system theming.
- ⚙️ **Persistent settings** — Interval, theme, hotkey, and the toggles are remembered across runs
(`%USERPROFILE%\.mouse_utility_config.json`).

### Notes

📦 **Unsigned installer**: Ships as an unsigned `.exe` installer, so Windows SmartScreen may
display a warning the first time you run it.

⚠️ **Fair warning**: I'm not a software developer. This project was developed primarily using
Claude Code, with me directing the architecture, reviewing the generated code, testing, and making
iterative improvements. All code in this repository has been reviewed before release. The only
asset not created with AI is the app icon, which I designed myself.

## Requirements

- Windows 10 20H1 (build 19041) or later, Windows 11 recommended.
- .NET 8 SDK, to build from source. (Not required on the machine you *run* the published app or
  installer on — both are self-contained.)

## Build & run (development)

```powershell
cd MouseUtil
.\BuildAndRun.ps1 MouseUtil.csproj
```

This restores, builds (Debug/x64 by default), and launches the app via `winapp run`. Pass
`-SkipRun` to build only, or override config/platform with MSBuild-style args, e.g.
`.\BuildAndRun.ps1 MouseUtil.csproj -SkipRun /p:Configuration=Release`.

## Building the installer

The installer (`installer\MouseUtil.iss`) packages the self-contained **Release** build with Inno
Setup into a per-user installer — no admin rights required, adds a Start Menu entry and
uninstaller.

```powershell
.\BuildAndRun.ps1 MouseUtil.csproj -SkipRun /p:Configuration=Release
ISCC.exe installer\MouseUtil.iss
```

Output lands at `installer\Output\MouseUtilSetup.exe`. The app version shown in the Settings
flyout is read from the running exe's file version, which is kept in sync between
`MouseUtil.csproj`'s `<Version>` and `MouseUtil.iss`'s `MyAppVersion` (currently `1.2.2`).

## Publish (portable folder, no installer)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Output lands in `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`. Zip that folder up
or copy it anywhere — `MouseUtil.exe` runs standalone, no installer, no other machine-wide
prerequisites.

For ARM64 devices, swap `-r win-x64` for `-r win-arm64` (the project already declares both
platforms/RIDs).

## If you want an MSIX-packaged version instead

This project intentionally skips MSIX packaging in favor of the unpackaged + Inno Setup installer
combo above. To convert it to MSIX instead:

1. Add a `Package.appxmanifest` (e.g. via **Project > Add > New Item > Application Manifest**, or
   scaffold a fresh `dotnet new winui-mvvm` project and diff its manifest in).
2. In `MouseUtil.csproj`, remove `WindowsPackageType=None` and `EnableMsixTooling=false`, and drop
   `WindowsAppSDKSelfContained`/`SelfContained` (or set them to match a framework-dependent
   packaged deployment if you don't want to bundle the runtime).
3. Build/deploy via `winapp run` or `Package.appxmanifest`-aware tooling, which requires Developer
   Mode enabled on the machine (Settings > System > For developers).
4. To ship through the Microsoft Store or via sideloading, sign the resulting MSIX with a
   certificate trusted on the target machine.

## Usage

- **Mode switch**: switch between **Click** and **Spin**.
- **Interval**: minutes + seconds.
- **Auto Stop** (checkbox + button): configure a stop condition — after a number of clicks/spins,
  or at a specific date/time. Must be explicitly (re)confirmed each session before a run will use
  it.
- **Settings** (gear icon): Light/Dark/System theme, global Start/Stop hotkey, action counter,
  close-to-tray, and taskbar progress toggles, plus Spin mode's "Pause on movement" toggle.
- **ON/OFF pill**: starts/stops the utility, or shows a running click/spin count if the action
  counter is enabled. Turning on always waits a fixed 3-second grace period before the first
  action (unless started via the global hotkey), regardless of the interval.
- **System tray**: if "Close to tray" is enabled, closing the window hides it to the tray instead
  of exiting; the tray icon's context menu can restore the window or exit the app.

### Status line reference

| Text | Color | Meaning |
|---|---|---|
| Starting in Xs | accent/green | Fixed 3-second grace period after turning on. |
| Clicking/Spinning in Xs | muted | Normal countdown to the next action. |
| Clicking/Spinning in Xs | red | Last 3 seconds before the action fires. |
| Paused | orange | Spin mode only — countdown frozen because the mouse is being moved. |
| Paused... Resuming in Xs | orange | After 5s of stillness, a live countdown for the rest of the required still period. |

**Pause on movement** (Spin mode only, toggle in Settings): if you move the mouse yourself during
the countdown, the timer pauses instead of firing mid-movement. The mouse has to sit still for the
*entire* current interval (not just a few seconds) before it resumes — once that's reached, a spin
fires immediately and a fresh full-length countdown begins.

## Project layout

```
MouseUtil.csproj       Unpackaged/self-contained project settings
app.manifest           DPI awareness / OS compatibility manifest
BuildAndRun.ps1        Build (+ optionally run) helper script
installer\MouseUtil.iss           Inno Setup script producing the per-user installer
App.xaml(.cs)          Application entry point
MainWindow.xaml(.cs)   UI, Mica backdrop, custom title bar, theming
Controls/StatusLabel.cs       Themed status-line control (Muted/Success/Caution/Critical tones)
Controls/DimmableLabel.cs     Caption label that dims declaratively when its target control is disabled
Interop/NativeMethods.cs      All Win32 P/Invoke (SetCursorPos, SendInput, GetDpiForWindow, hotkeys, etc.)
Models/AppConfig.cs           Persisted settings shape
Services/ConfigService.cs             Reload-modify-save JSON persistence
Services/MouseAutomationEngine.cs     Background state machine (timing, pause-on-movement, spin sweep, auto stop)
Services/GlobalHotkeyService.cs       RegisterHotKey-based global Start/Stop hotkey
Services/TrayIconService.cs           Shell_NotifyIcon-based system tray icon and context menu
Services/TaskbarProgressService.cs    ITaskbarList3-based taskbar icon progress overlay
Services/SingleInstanceService.cs     Single-instance enforcement (refocuses the existing window)
```
