# MouseUtil

A native Windows 11 mouse-activity utility built with WinUI 3 (Windows App SDK) and .NET 8. It runs
in one of two modes on a timer:

- **Click** — clicks at wherever the cursor currently is.
- **Spin** — sweeps the cursor around a tiny ~12px circle and returns it to the exact starting
  pixel, without clicking. Useful for keeping a machine from going idle/locking/sleeping.

Ships as an **unpackaged, self-contained** app: no MSIX, no Store install, no Developer Mode
requirement. A per-user Inno Setup installer is provided for easy distribution, or you can run the
`dotnet publish` output directly as a portable folder.

## Features

- **Interval timer** — minutes + seconds, down to a 0.05s minimum total.
- **Auto Stop** (optional) — stop automatically after a configured number of clicks/spins, or at a
  specific date/time.
- **Pause on movement** (Spin mode) — pauses the countdown while you move the mouse yourself, and
  requires the mouse to sit still for a full interval before resuming.
- **Global hotkey** — default `F6`, re-recordable in Settings, to start/stop from anywhere without
  focusing the window.
- **Action counter** — optionally shows a running click/spin count on the Start/Stop button and a
  "Stopped after N clicks/spins" summary when a run ends.
- **Close to tray** — optionally hides to the system tray instead of exiting when the window is
  closed, with a tray icon menu to restore or exit.
- **Taskbar progress** — optionally mirrors the countdown as a progress overlay on the taskbar icon.
- **Light/Dark/System theme**, close confirmation while automation is running, and single-instance
  enforcement (a second launch just refocuses the existing window).

Interval, theme, hotkey, and the toggles above are remembered across runs
(`%USERPROFILE%\.mouse_utility_config.json`).

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
`MouseUtil.csproj`'s `<Version>` and `MouseUtil.iss`'s `MyAppVersion` (currently `1.2.1`).

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
