# Changelog

All notable changes to MouseUtil are documented in this file.

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
