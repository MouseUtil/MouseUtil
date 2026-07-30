# Changelog

All notable changes to MouseUtil are documented in this file.

## [1.2.2] - 2026-07-29

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

## [1.2.1] - 2026-07-28

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
