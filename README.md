# MouseUtil

MouseUtil is a lightweight Windows utility with two main tools, an auto clicker and a mouse jiggler,
that can automate simple interactions or repetitive tasks and keep your PC active during long waits
or AFK sessions.

![MouseUtil](Assets/hero.gif)

<a href="https://apps.microsoft.com/detail/9NTDCVFVF0SQ?referrer=appbadge&mode=full" target="_blank" rel="noopener noreferrer">
    <picture>
        <source media="(prefers-color-scheme: dark)" srcset="https://get.microsoft.com/images/en-us%20dark.svg">
        <img src="https://get.microsoft.com/images/en-us%20light.svg" width="300"/>
    </picture>
</a>

### Modes

- **Auto click** - Automatically clicks where the mouse pointer currently is on the screen.
- **Jiggle** - Moves the cursor in a tiny circle and lands back on the exact starting pixel.

### Features and options

⏱️ **Configurable interval** between actions, shown as Minutes and Seconds (millisecond precision, 3
decimal places) or broken out into separate Hours/Minutes/Seconds/Milliseconds fields.

🎲 **Randomized intervals** - optionally enable so each countdown is a random duration instead of a
fixed one. Your configured interval becomes the cap; the actual wait is drawn between that and a
small floor (10% of the interval, or 250ms, whichever is larger).

✋ **Pause on manual movement** - touching the mouse pauses the countdown instead of fighting you for
control. Can be turned on independently for Auto click and/or Jiggle.

🛑 **Auto-stop** after a number of clicks/jiggles, or at a specific date and time.

⌨️ **Global hotkey** to start/stop from anywhere (F6 by default).

📊 **Live countdown or action counter** right on the Start/Stop button, plus an optional taskbar
progress overlay.

📥 **System tray support** - optionally close to tray instead of exiting, with a live-status icon
and a context menu to start/stop without opening the window.

🚀 **Run on system startup**, with an independent "App launch behavior" (preferred mode, start
automatically, randomize interval) that can be applied on every launch.

🎨 **Fluent design** with light/dark/system theming.

⚙️ **Settings persist** across runs (`%APPDATA%\MouseUtil\config.json`).

### Notes

⚠️ **Fair warning**: I'm not a software developer. This project was developed primarily using
Claude Code, with me directing the architecture, reviewing the generated code, testing, and
making iterative improvements. All code in this repository has been reviewed before release. The
only asset not created with AI is the app icon, which I designed myself.

## Building from source

### Requirements

- Windows 10 20H1 (build 19041) or later, Windows 11 recommended.
- .NET 8 SDK, only if building from source.

### Build & run

```powershell
dotnet build MouseUtil.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
.\.output\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MouseUtil.exe
```
