# MouseUtil

A native Windows 11 mouse-activity utility built with WinUI 3 (Windows App SDK) and .NET 8. It runs
in one of two modes on a timer:

- **Click** — clicks at wherever the cursor currently is.
- **Spin** — sweeps the cursor around a tiny ~12px circle and returns it to the exact starting
  pixel, without clicking. Useful for keeping a machine from going idle/locking/sleeping.

Ships as an **unpackaged, self-contained** app: no MSIX, no Store install, no Developer Mode
requirement. `dotnet publish` produces a folder you can copy anywhere and run directly, like a
portable exe.

## Requirements

- Windows 10 20H1 (build 19041) or later, Windows 11 recommended.
- .NET 8 SDK, to build from source. (Not required on the machine you *run* the published app on —
  the publish output is self-contained.)

## Build & run (development)

```powershell
cd MouseUtil
dotnet build -p:Platform=x64
```

Run the built exe directly — this is an unpackaged app, so there's no `winapp run` / MSIX install
step:

```powershell
.\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MouseUtil.exe
```

## Publish (portable folder)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Output lands in `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`. Zip that folder up
or copy it anywhere — `MouseUtil.exe` runs standalone, no installer, no other machine-wide
prerequisites.

For ARM64 devices, swap `-r win-x64` for `-r win-arm64` (the project already declares both
platforms/RIDs).

## If you want an MSIX-packaged version instead

This project intentionally skips packaging. To convert it:

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

- **Mode pill** (top right, in the title bar): switch between **Click** and **Spin**.
- **Interval**: minutes + seconds. Minimum total interval is 0.05s.
- **Automatically stop at**: optional date/time after which the utility turns itself off.
- **Settings** (gear icon): Light/Dark/System theme, and Spin mode's "Pause on movement" toggle.
- **ON/OFF pill**: starts/stops the utility. Turning on always waits a fixed 3-second grace period
  before the first action, regardless of the interval.

Interval, theme, and "pause on movement" are remembered across runs
(`%USERPROFILE%\.mouse_utility_config.json`).

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
App.xaml(.cs)          Application entry point
MainWindow.xaml(.cs)   UI, Mica backdrop, custom title bar, theming
Interop/NativeMethods.cs      All Win32 P/Invoke (SetCursorPos, SendInput, GetDpiForWindow)
Models/AppConfig.cs           Persisted settings shape
Services/ConfigService.cs     Reload-modify-save JSON persistence
Services/MouseAutomationEngine.cs   Background state machine (timing, pause-on-movement, spin sweep)
```
