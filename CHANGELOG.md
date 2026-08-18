# Changelog

All notable changes to MouseUtil are documented in this file.

## [1.3.0]

### Added

- **System tray icon now shows 4 distinct states instead of one static icon.** Inactive, Active,
  and Paused (Spin mode's pause-on-movement) are now visually distinguishable at a glance without
  opening the app - matching the pause indicator already shown in the main window and on the
  taskbar icon. Inactive uses a simple black-and-white glyph that follows the *system taskbar*
  theme (not the app's own theme setting), swapping automatically if you change your Windows
  light/dark taskbar setting while the app is running - Active and Paused use full-color icons
  that read correctly on either a light or dark taskbar, so they don't need theme variants.

- **Tray icon tooltip now reports live status.** Hovering over the tray icon shows the current
  mode and state, e.g. "MouseUtil: Auto click - Active" or "MouseUtil: Spin mode - Paused",
  updating immediately on start/stop/pause and even when switching modes while inactive.

- **Tray context menu can now control automation directly, without opening the window.** Added
  Start Auto Click, Start Spin Mode, Stop, and a Pause spinning on movement toggle. The two Start
  options are only enabled while automation is inactive (regardless of which mode is currently
  selected in the main window); once running, only Stop is enabled, and the pause toggle is
  disabled for the duration of the run - preventing a start-while-running mode conflict. Starting
  Auto Click from the tray also skips the startup countdown, matching the existing hotkey
  behavior.

- **Settings has been completely rebuilt as a single in-window Settings view, replacing the old
  flyout entirely.** Clicking the gear button no longer opens a small popup flyout - it now opens
  a full-page Settings view over the main window's content, with a "Back" link at the top instead
  of dismissing like a flyout. Settings is also reorganized into three labeled, card-styled
  sections (Interface, General, Update) matching the visual style already used for the Interval
  and Auto Stop cards, instead of one flat unlabeled list of rows. The Theme selector no longer
  shows the "Theme" caption above the dropdown - just the dropdown itself. A manual "Check for
  Updates" button was added under the Update section (see below).

- **Manual "Check for Updates" button, under Settings' new Update section.** Checks the project's
  GitHub Releases, and if a newer version is available, relabels itself (with an accent color and
  a download icon) to "Update to vX.Y.Z" - clicking it again downloads the installer and launches
  it, closing MouseUtil so the installer can overwrite its files. If the release has no installer
  asset yet, it opens the release page in your browser instead.

- **Optional automatic update check at launch**, via a new "Check for updates on launch" toggle
  next to the manual button above (default off). When enabled, MouseUtil silently checks GitHub
  Releases once at startup, and if a newer version is found, shows a small accent-colored download
  icon next to the Settings gear - clicking it opens a flyout with a one-click "Update to vX.Y.Z"
  button. Dismissing the flyout without updating hides the icon again for the rest of the session;
  checking again afterward requires the manual button, which always starts idle regardless of what
  the automatic check found.

- **Auto Stop's date/time summary now shows a relative-day label**, e.g. "Yesterday 18:00",
  "Today 18:00", or "Tomorrow 18:00" when applicable, instead of always showing the full date -
  refreshed automatically at midnight so it stays accurate during a long-running session.

- **Optional randomized interval between clicks/spins**, via a new small toggle next to the
  Interval card's "INTERVAL" label. When enabled, each action fires after a randomly-picked gap
  instead of the fixed interval - uniformly between the configured interval (used as the maximum)
  and a floor of whichever is larger, 10% of that interval or 250ms. Applies to both Click and Spin
  mode; locked while automation is running, and always starts off at launch. Spin mode's
  pause-on-movement resume timing is unaffected and still waits for the full configured interval
  regardless of this setting.

- **Countdown status text now uses minutes and hours for longer intervals**, e.g. "Spinning in
  1m 30s" or "Clicking in 1h 5m 0s" instead of "Spinning in 90s" or "Clicking in 3900s". Applies
  everywhere a countdown is shown - the running countdown itself, "Paused... Resuming in", and the
  startup countdown. Under a minute is unchanged (still plain seconds); the underlying countdown
  value and tick rate are untouched, only the display format changes once it gets long enough that
  raw seconds become hard to read at a glance.

- **New "Display advanced interval" option, under Settings' Interface section, shows the interval
  as Hours/Minutes/Seconds/Milliseconds fields instead of the plain Minutes/Seconds ones.**
  Toggling it swaps the Interval card's fields in place, converting whatever's currently configured
  (e.g. 90 minutes becomes 1h 30m 0s 0ms) rather than resetting it, and does the reverse conversion
  when switched back off. Locked while automation is running, same as the interval fields
  themselves. Off by default.

  The Basic (Minutes/Seconds) and Advanced (Hours/Minutes/Seconds/Milliseconds) field rows render
  at a couple pixels' difference in height, so both are now pinned to the same bottom-anchored row
  inside the card, with the Interval card's Border given a `MinHeight="130"` floor - any height
  difference between the two layouts shows up as extra space above the fields instead of changing
  the card's total height (and shifting the Auto Stop card underneath it) whenever the toggle is
  switched.

- **Both interval field layouts now validate and truncate input instead of silently accepting
  anything typed.** Decimal values are truncated (floored, never rounded - e.g. 56.8 becomes 56,
  not 57) rather than accepted as-is: the four Advanced fields and Basic's Minutes don't allow
  decimals at all, and Basic's Seconds is capped at 3 decimal places (5.6427 becomes 5.642). Each
  field also has a maximum it can't be typed or clamped past - Hours up to 1,666,665, Advanced
  Minutes/Seconds up to 59, Milliseconds up to 999 (Basic's Minutes/Seconds maximums, 99,999,959 and
  59.999, were raised to match this same combined ceiling) - plus a matching cap on how many
  characters can even be typed into each field. No field can be left blank either: clearing one
  (Backspace/Delete/Ctrl+A+Delete, or Basic's Minutes/Seconds "X" clear button, which resets to 0
  instead of leaving the field empty - the Advanced fields don't have a clear button, since the same
  built-in WinUI button turned out to be unresponsive there) immediately snaps it back to 0. All of
  this holds even for a hotkey-triggered start, which never gives any field a chance to lose focus -
  the value actually used to start automation always reflects the same truncated/clamped/defaulted
  number the field is currently showing, never a stale or uncommitted one.

- **All six interval fields now only accept typed digits, rejecting anything else as you type**
  instead of silently accepting it and only catching it later at commit/truncation. Typing a letter,
  symbol, or minus sign into Hours/Minutes/Seconds/Milliseconds or Basic's Minutes/Seconds simply
  does nothing. Basic's Seconds is the one exception, since it's the only field that supports
  decimal values - it also accepts a single "." while typing; a second "." is rejected the same as
  any other invalid character. The four Advanced fields and Basic's Minutes don't accept "." at all,
  matching the fact that none of them support fractional values. Basic's Seconds also accepts ","
  as a decimal separator, typed anywhere a "." would work - it's converted to "." live as you type,
  so the field always displays and behaves as if "." had been typed instead.

- **The "After a number of clicks/spins" field in the Automatically Stop dialog now only accepts
  typed digits**, the same as the interval fields above - letters, symbols, and a decimal point/comma
  are all rejected as you type, since this field is always a whole count. Its maximum was lowered
  from 100,000,000 to 99,999,999, with a matching 8-character typing cap.

### Changed

- **Auto click's mode-switch icon changed from a hand-drawn plain arrow to a Segoe Fluent Icons
  cursor-click glyph**, matching the icon language used elsewhere in the app instead of a
  custom-drawn shape. Rendered slightly larger than the neighboring Spin mode icon, to compensate
  for the glyph reading visually smaller than the Sync symbol at matching sizes.

- **The mode-switch button's tooltip was renamed from "Switch between Click mode and Spin mode" to
  "Switch between Auto click and Spin mode"**, matching the mode name shown everywhere else in the
  app (the title bar subtitle, the tray menu, etc.), which all call it "Auto click" rather than
  "Click mode."

- **The Automatically Stop summary button's icon was updated**, and now renders at reduced (0.6)
  opacity to read as a subtler, more secondary accent next to its label text. All three of its states
  changed glyph: the clicks/spins mode's icon changed from a refresh glyph to a replay glyph, the
  date/time mode's icon changed from a calendar glyph to a dedicated date-time glyph, and the
  unconfigured/placeholder state's icon changed from a pencil/edit glyph to a "mini expand" glyph.

### Fixed

- **Generic icon shown in Alt-Tab, Task View, and taskbar previews.** WinUI 3's window doesn't
  automatically bind the exe's embedded icon to the running window's own icon, so those OS-level
  surfaces fell back to a generic default instead of the app icon already used for the tray icon.

- **Pressing the current hotkey while recording a new one in Settings triggered Start/Stop instead
  of being captured.** The global hotkey stayed registered during recording, so the OS intercepted
  that keypress as WM_HOTKEY instead of delivering it to the recorder - silently starting/stopping
  automation and leaving the recorder stuck on "Press a key combination..." until Settings was
  closed and reopened. The current hotkey is now unregistered for the duration of the capture.

- **Clicking straight into the Minutes field as your first action after launch silently moved
  focus to Seconds instead.** A one-time startup redirect had existed to move WinUI's automatic
  initial focus away from Minutes to Seconds, since it would otherwise visually block part of the
  Seconds field's own value. Adding the randomize-interval toggle above gave the Interval card an
  earlier control in tab order, so WinUI's initial focus started landing there instead - the old
  redirect no longer had anything to intercept at startup, and sat armed to hijack your first
  genuine click into Minutes instead. Removed, now that it's no longer needed.

## [1.2.2]

### Fixed

- **Timer drift in both Click mode and Spin mode.** With a long-running session, the actual
  interval between actions ran measurably slower than the interval you configured - actions
  fired noticeably less often than expected. For example, with a 1-minute interval, a 15-minute
  run performed only 14 actions instead of the expected 16 (1 immediate action on start, plus
  one every 60s for 15 minutes). The effective interval was drifting to roughly 64-65 real
  seconds per nominal 60-second interval - small per tick, but compounding steadily over a run.

  **Root cause:** the countdown was tracked as a `TimeSpan` counter that started at the
  configured interval and was decremented by a *fixed nominal* 100ms every loop iteration,
  immediately after `await Task.Delay(100)`. `Task.Delay` isn't guaranteed to return in exactly
  100ms - on Windows it can overshoot due to timer resolution, plus whatever time is spent
  reporting status/dispatching events each iteration - and none of that overshoot was ever
  accounted for. The loop always assumed exactly 100ms had passed, so real overshoot was
  silently dropped every tick. With a 60s interval that's about 600 ticks per action; the small
  per-tick loss compounded into minutes of real drift over a long run. This affected Click and
  Spin mode identically, since both share the same countdown loop, and also affected the
  pause-on-movement "stillness" countdown and the 3-second startup grace period, which used the
  same pattern.

  **Fix:** the countdown now measures actual elapsed time off a `Stopwatch` instead of
  decrementing a nominal `TimeSpan` counter. A `Stopwatch` is started once per interval, and each
  loop iteration re-derives "how much time is left" as `interval - stopwatch.Elapsed`, sleeping
  only up to that remaining amount (capped at the 100ms tick) rather than always sleeping the
  full tick regardless of how much time is actually left. When an action fires, the stopwatch is
  restarted from that exact moment, so drift can no longer compound tick-to-tick or run-to-run -
  every interval is measured fresh against real elapsed time instead of building on a running
  total of assumptions. The pause-on-movement stillness countdown and the 3-second startup grace
  period were changed the same way, using their own `Stopwatch` instances. Status text,
  progress-bar behavior, and countdown display formatting are all unchanged - only the underlying
  source of truth for elapsed/remaining time changed.

- **Residual Spin-mode drift, left over after the fix above.** Even with the `Stopwatch`-based
  deadline in place, Spin mode's actual interval between spins still ran a little slower than
  configured on long sessions - on the order of a couple of seconds off over a 15-minute run at a
  60-second interval, much smaller than the drift fixed above but still present. Click mode was
  unaffected.

  **Root cause:** this was the same underlying bug as above, just in a different spot -
  `RunLoopAsync` only restarted the interval `Stopwatch` *after* that tick's action had fully
  fired and returned. Click mode's action (two `SendInput` calls) is sub-millisecond, so
  restarting the stopwatch a moment later than ideal made no visible difference there. But Spin
  mode's action is `SpinSweep`, a cursor-sweep animation that blocks synchronously for real
  wall-clock time (16 steps x 12ms = ~192ms) before returning - and since the stopwatch wasn't
  restarted until after that ~192ms had already elapsed, it was never counted against anything.
  It was pure extra time silently added on top of every single cycle.

  **Fix:** the interval `Stopwatch` (and its equivalent in the pause-on-movement "stood still"
  branch, plus the initial pre-loop stopwatch creation before the first action fires) is now
  restarted immediately before the action fires, instead of after. The blocking time the action
  itself takes now counts against the *next* interval rather than stacking on top of the current
  one, so the gap between two consecutive actions matches the configured interval in both modes.

### Changed

- **Installer no longer shows the version number in the app's display name.**
  "Installed apps" entry previously appeared as "MouseUtil version 1.x.x" instead of
  "MouseUtil". Inno Setup derives a separate `AppVerName` value for these labels and defaults it
  to `"{AppName} {AppVersion}"` when not set explicitly - the installer script only set `AppName`
  and `AppVersion` separately, so it fell back to that combined default.

  **Fix:** `installer\MouseUtil.iss` now sets `AppVerName=MouseUtil` explicitly, so all UI elements
  just read "MouseUtil". The version is still visible in its dedicated spot in "Installed Apps".

- **Installer Welcome page restored, now showing the version explicitly.** The installer previously
  skipped straight past the Welcome page.

  **Fix:** `DisableWelcomePage=no` was added to `installer\MouseUtil.iss` to bring the Welcome
  page back, along with a custom message so the version is still visible there, independent of
  the now-unversioned `AppVerName`.

### Internal

- Only `Services\MouseAutomationEngine.cs` changed: `RunLoopAsync` and `RunStartupGraceAsync`
  now track elapsed time via `Stopwatch` instead of nominal tick-decremented `TimeSpan` counters.
  `SpinSweep` (the cursor-movement sub-loop), which already used a `Stopwatch` correctly, is
  untouched.
- `RunLoopAsync` further changed: the interval `Stopwatch` is now restarted immediately before
  firing the action rather than after, in all three places it fires one (the pre-loop initial
  fire, the main per-tick fire, and the pause-on-movement stillness fire), so a blocking action
  (Spin mode's `SpinSweep`) counts its own duration against the next interval instead of adding
  it on top of every cycle.
- `installer\MouseUtil.iss`: added `AppVerName`, `DisableWelcomePage=no`, and a `[Messages]`
  section overriding `WelcomeLabel1`. No app code changed for these.

## [1.2.1]

### Changed

- Renamed the "Countdown display in taskbar" setting to "Show countdown progress on taskbar
  icon" - the old caption was ambiguous about whether it showed a number/text somewhere
  rather than a progress bar directly on the app's taskbar icon.
- Small update to the app icon.

### Fixed

- **Hotkey-triggered start could use a stale Minutes/Seconds interval.** If you typed a new
  Minutes or Seconds value and pressed the Start/Stop hotkey (default F6) immediately
  afterward — without clicking away from the field or pressing Enter first — automation
  would start using the *previous* interval instead of the one you just typed, even though
  the field visually displayed the new value. Using the spin buttons (the up/down arrows)
  to change the interval was never affected; only typed input was.

  This was purely a read-timing bug in how the interval was captured at start time - the
  hotkey itself, and the app's ability to start/stop automation while unfocused (e.g. while
  another app is in the foreground), are unchanged. It required an unusually fast
  type-then-hotkey sequence to hit reliably, and is unlikely to have visibly affected slower,
  more deliberate use.

  **Root cause:** `NumberBox` only commits typed text into its `Value` property on focus
  loss or Enter - clicking the spin buttons commits instantly, typing does not. Reading
  `NumberBox.Value` directly at hotkey-triggered start time therefore missed any not-yet-committed
  typed input. Reading `NumberBox.Text` instead (an initial fix attempt) turned out to be
  insufficient too - that property was measured to lag the visibly-typed text by 60ms or more
  in some cases, likely due to internal validation being handled asynchronously; a global
  hotkey delivered via `RegisterHotKey`/`WM_HOTKEY` can be processed by the app before that
  internal sync finishes, even though both run on the UI thread.

  **Fix:** the interval is now read directly from the `NumberBox`'s own internal input
  TextBox (its `InputBox` template part) at start time, which reflects exactly what's been
  typed with no propagation delay, falling back to `NumberBox.Text` if that part can't be
  found for any reason. Verified with automated zero-delay repro trials (type a value,
  immediately trigger the hotkey, no pause) across multiple values and both the Minutes and
  Seconds fields.

### Internal

- No changes to hotkey registration/delivery (`Services\GlobalHotkeyService.cs` untouched) -
  the global hotkey still fires identically regardless of window focus.
- Only `MainWindow.xaml.cs` changed: `PowerToggleButton_Checked`'s interval read now goes
  through a new `ReadCommittedOrTypedValue`/`FindInputBoxText` helper pair instead of reading
  `NumberBox.Value` directly.

## [1.2.0]

### Added

- Taskbar icon progress indicator, giving at-a-glance status visibility without needing to
  open the app window.

### Changed

- Slight changes to the app icon to fit better in the system tray.

## [1.1.2]

### Added

- App version now shown in the settings flyout header.

### Changed

- Further tweaks to the settings UI.

### Fixed

- **Spin mode status could get stuck on a stale countdown after a quick stop.** Stopping
  automation shortly after starting could leave the status showing "Spinning in Xs" instead of
  "Stopped after N spin" - caused by the 500ms display-hold timer added in 1.1.0, which didn't
  know Stop had been pressed.

## [1.1.1]

### Changed

- Minor tweaks to the settings UI.

## [1.1.0]

### Added

- **Close to system tray** (optional, off by default): closing the window now keeps MouseUtil
  running in the tray instead of exiting.
- **Single instance**: launching MouseUtil while it's already running brings the existing window
  to the front instead of opening a second copy.

### Changed

- Spin mode: removed the unnecessary startup delay before the first spin (still present for
  Auto Click, where it prevents an accidental instant stop).

## [1.0.0]

### Added

- **Two automation modes** - **Auto click**, which sends a real left-click at a set interval,
  or **Spin mode**, which sweeps/jiggles the cursor in a tiny circle and returns it to its exact
  starting pixel.
- **Configurable interval** - set the delay between actions (clicks or spins) in minutes and seconds.
- **Pause on manual movement** (in Spin mode) - if you touch the mouse yourself, MouseUtil
  automatically pauses and resumes on a fresh countdown once you stop, so it never fights you for
  control.
- **Auto-stop conditions** - stop the run automatically after a set number of clicks/spins, or at a
  specific date and time.
- **Global hotkey** - start or stop automation from anywhere with a single keypress (F6 by default),
  even while another app is focused.
- **Live status and progress** - a real-time countdown ("Clicking in 12s") and an action counter.
- **Windows 11 native** - Fluent design with light/dark/system theming. Build with WinUI 3 and .NET 8.
- **Persisted config file**, so your interval, mode, hotkey, and preferences are remembered between
  sessions.