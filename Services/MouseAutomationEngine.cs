using System.Diagnostics;
using MouseUtil.Interop;

namespace MouseUtil.Services;

public enum AutomationMode
{
    Click,
    Spin
}

public enum StatusKind
{
    Off,
    Starting,
    Running,
    Imminent,
    Paused,

    /// <summary>
    /// The one-shot "Starting now" reported synchronously by Start() for Spin mode (which has no
    /// startup grace countdown to report through). Distinct from Starting so MainWindow can apply a
    /// minimum on-screen hold to just this report, without affecting Click mode's real-time
    /// "Starting in Xs" countdown (which also uses Starting).
    /// </summary>
    SpinStarting
}

public sealed class StatusChangedEventArgs : EventArgs
{
    public StatusChangedEventArgs(string text, StatusKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }
    public StatusKind Kind { get; }
}

public sealed class ActionPerformedEventArgs : EventArgs
{
    public ActionPerformedEventArgs(AutomationMode mode)
    {
        Mode = mode;
    }

    public AutomationMode Mode { get; }
}

/// <summary>
/// Runs the click/spin state machine on a background task. All timing decisions happen here;
/// callers only ever see <see cref="StatusChanged"/> notifications and must marshal them to the UI thread.
/// </summary>
public sealed class MouseAutomationEngine
{
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ImminentThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StillnessDisplayThreshold = TimeSpan.FromSeconds(5);
    private const int TickMilliseconds = 100;

    // Below this, the countdown's last "in 0.1s" tick is replaced by "Starting now" / "Clicking
    // now" / "Spinning now" instead - see FormatCountdownStatus. Purely a status-text display
    // choice; never affects when the next action actually fires.
    private static readonly TimeSpan CountdownLabelThreshold = TimeSpan.FromMilliseconds(TickMilliseconds);
    private const int SpinRadiusPixels = 6; // ~12px diameter circle
    private const int SpinSteps = 16;
    private const double SpinStepDurationMs = 12.0;

    private static readonly TimeSpan FirstClickSelfStopWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _autoMoving;

    // UTC ticks of when this run's very first Click-mode action was injected, or 0 if that hasn't
    // happened yet. Stamped exactly once per run (see FireAction/Start) - never updated again for
    // any later action - so MainWindow (WasFirstClickJustInjected) can tell "the very first click
    // just landed on and toggled off the Start/Stop button" apart from any later self-inflicted
    // click, which always falls outside FirstClickSelfStopWindow since this timestamp is frozen.
    // Stamped immediately before injecting the click (not after) so a self-inflicted stop - which
    // can arrive back on the UI thread essentially instantly - still sees it already set. Behind
    // Interlocked since it's written from this engine's background loop thread and read from the
    // UI thread.
    private long _firstClickInjectedTicks;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised when the loop stops itself (scheduled stop time reached) rather than via Stop().</summary>
    public event EventHandler? AutoStopped;

    /// <summary>
    /// Raised every time FireAction completes - every action this engine performs is counted by
    /// subscribers, including the first one fired immediately after the startup grace period (or
    /// immediately on Start when <see cref="Start"/>'s skipStartupCountdown is true). Fires from this
    /// engine's background loop thread, same as StatusChanged - callers must marshal to the UI thread
    /// before touching UI state.
    /// </summary>
    public event EventHandler<ActionPerformedEventArgs>? ActionPerformed;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts the automation loop. skipStartupCountdown bypasses the usual StartupGracePeriod
    /// countdown - used when Start is triggered via the global hotkey (see
    /// MainWindow.HotkeyService_HotkeyPressed), which performs the first action immediately instead
    /// of waiting, unlike a normal Start-button press. Spin mode always skips the countdown
    /// regardless of this flag - see the needsStartupGrace check in RunLoopAsync.
    /// </summary>
    public void Start(AutomationMode mode, TimeSpan interval, DateTime? stopAt, int? stopAfterActionCount, bool pauseOnMovementEnabled, bool skipStartupCountdown = false)
    {
        lock (_gate)
        {
            if (_cts != null)
            {
                return;
            }

            IsRunning = true;
            Interlocked.Exchange(ref _firstClickInjectedTicks, 0);

            // Spin mode skips RunStartupGraceAsync (see needsStartupGrace in RunLoopAsync), so
            // without this, the first status report wouldn't happen until the background task is
            // scheduled and fires the first spin - leaving a brief window where the UI still shows
            // the previous run's stale "Stopped after N spins" text. Reporting synchronously here,
            // before Task.Run, closes that gap instead of racing it.
            if (mode == AutomationMode.Spin)
            {
                ReportStatus("Starting now", StatusKind.SpinStarting);
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loopTask = Task.Run(() => RunLoopAsync(mode, interval, stopAt, stopAfterActionCount, pauseOnMovementEnabled, skipStartupCountdown, cts.Token), cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loopTask = null;
            IsRunning = false;
        }

        cts?.Cancel();
    }

    private async Task RunLoopAsync(AutomationMode mode, TimeSpan interval, DateTime? stopAt, int? stopAfterActionCount, bool pauseOnMovementEnabled, bool skipStartupCountdown, CancellationToken token)
    {
        // Counts actions fired during this run so far, checked against stopAfterActionCount right
        // after each FireAction call (as opposed to IsStopTimeReached below, which is checked BEFORE
        // deciding to fire the next action) - the count can only ever reach its target immediately
        // after firing the action that reaches it. Returns true if the caller should stop the loop.
        var actionsFired = 0;
        bool FireAndCheckActionCountStop()
        {
            FireAction(mode, token);
            actionsFired++;
            if (stopAfterActionCount.HasValue && actionsFired >= stopAfterActionCount.Value)
            {
                NotifyAutoStopped();
                return true;
            }

            return false;
        }

        try
        {
            // Only Click mode started via the Start button needs the startup grace period - it
            // exists so that click doesn't get immediately consumed as the first auto-click/stop.
            // Spin mode has no click-to-stop race to guard against, and a hotkey-triggered start
            // (either mode) assumes the mouse isn't hovering the Start button, so both skip it.
            var needsStartupGrace = !skipStartupCountdown && mode == AutomationMode.Click;
            if (needsStartupGrace && !await RunStartupGraceAsync(stopAt, token).ConfigureAwait(false))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (FireAndCheckActionCountStop())
            {
                return;
            }

            var remaining = interval;
            var stillness = TimeSpan.Zero;
            var paused = false;
            var lastPos = NativeMethods.GetCursorPosition();

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (IsStopTimeReached(stopAt))
                {
                    NotifyAutoStopped();
                    return;
                }

                var verb = mode == AutomationMode.Click ? "Clicking" : "Spinning";
                var pauseOnMovementActive = mode == AutomationMode.Spin && pauseOnMovementEnabled;
                var manualMovementDetected = false;

                if (pauseOnMovementActive)
                {
                    var pos = NativeMethods.GetCursorPosition();
                    if (!_autoMoving && (pos.X != lastPos.X || pos.Y != lastPos.Y))
                    {
                        manualMovementDetected = true;
                    }

                    lastPos = pos;
                }

                if (pauseOnMovementActive && (paused || manualMovementDetected))
                {
                    if (manualMovementDetected)
                    {
                        paused = true;
                        stillness = TimeSpan.Zero;
                        ReportStatus("Paused", StatusKind.Paused);
                    }
                    else
                    {
                        stillness += TimeSpan.FromMilliseconds(TickMilliseconds);

                        if (stillness >= interval)
                        {
                            // Stood still for the full interval: fire immediately, as if the timer had gone off,
                            // then start a fresh full-length countdown - never resume from where it froze.
                            paused = false;
                            stillness = TimeSpan.Zero;
                            if (FireAndCheckActionCountStop())
                            {
                                return;
                            }

                            lastPos = NativeMethods.GetCursorPosition();
                            remaining = interval;
                            await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
                            continue;
                        }

                        if (stillness >= StillnessDisplayThreshold)
                        {
                            var resumeRemaining = interval - stillness;
                            ReportStatus(FormatCountdownStatus($"{verb} now", resumeRemaining, $"Paused... Resuming in {FormatSeconds(resumeRemaining)}"), StatusKind.Paused);
                        }
                        else
                        {
                            ReportStatus("Paused", StatusKind.Paused);
                        }
                    }

                    await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
                    continue;
                }

                var kind = remaining <= ImminentThreshold ? StatusKind.Imminent : StatusKind.Running;
                ReportStatus(FormatCountdownStatus($"{verb} now", remaining, $"{verb} in {FormatSeconds(remaining)}"), kind);

                await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
                remaining -= TimeSpan.FromMilliseconds(TickMilliseconds);

                if (remaining <= TimeSpan.Zero)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (FireAndCheckActionCountStop())
                    {
                        return;
                    }

                    lastPos = NativeMethods.GetCursorPosition();
                    remaining = interval;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called - exit quietly.
        }
    }

    private async Task<bool> RunStartupGraceAsync(DateTime? stopAt, CancellationToken token)
    {
        var remaining = StartupGracePeriod;

        while (remaining > TimeSpan.Zero)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (IsStopTimeReached(stopAt))
            {
                NotifyAutoStopped();
                return false;
            }

            ReportStatus(FormatCountdownStatus("Starting now", remaining, $"Starting in {FormatSeconds(remaining)}"), StatusKind.Starting);
            await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
            remaining -= TimeSpan.FromMilliseconds(TickMilliseconds);
        }

        return true;
    }

    private void FireAction(AutomationMode mode, CancellationToken token)
    {
        // Guard against a race where Stop() lands right as an action is about to fire.
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (mode == AutomationMode.Click)
        {
            if (Interlocked.Read(ref _firstClickInjectedTicks) == 0)
            {
                Interlocked.Exchange(ref _firstClickInjectedTicks, DateTime.UtcNow.Ticks);
            }

            NativeMethods.SendLeftClick();
        }
        else
        {
            SpinSweep(token);
        }

        ActionPerformed?.Invoke(this, new ActionPerformedEventArgs(mode));
    }

    /// <summary>
    /// True only if this run's very first Click-mode action was injected within the last
    /// FirstClickSelfStopWindow - used solely to decide whether to show the "shrug" status text
    /// (see MainWindow.PowerToggleButton_Unchecked) when that first click happened to land on and
    /// toggle off the Start/Stop button; never to block or reverse a stop. Because
    /// _firstClickInjectedTicks is stamped exactly once per run, this can never return true for a
    /// stop caused by any later action, self-inflicted or otherwise - only the first one.
    /// </summary>
    public bool WasFirstClickJustInjected()
    {
        var ticks = Interlocked.Read(ref _firstClickInjectedTicks);
        if (ticks == 0)
        {
            return false;
        }

        return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= FirstClickSelfStopWindow;
    }

    private void SpinSweep(CancellationToken token)
    {
        var origin = NativeMethods.GetCursorPosition();
        _autoMoving = true;

        try
        {
            var stopwatch = Stopwatch.StartNew();

            for (var step = 1; step <= SpinSteps; step++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var angle = 2 * Math.PI * step / SpinSteps;
                var x = origin.X + (int)Math.Round(SpinRadiusPixels * Math.Cos(angle));
                var y = origin.Y + (int)Math.Round(SpinRadiusPixels * Math.Sin(angle));
                NativeMethods.MoveCursorTo(x, y);

                var targetElapsedMs = SpinStepDurationMs * step;
                while (stopwatch.Elapsed.TotalMilliseconds < targetElapsedMs)
                {
                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            // Always return to the exact original pixel, even if cancelled mid-sweep.
            NativeMethods.MoveCursorTo(origin.X, origin.Y);
            _autoMoving = false;
        }
    }

    private static bool IsStopTimeReached(DateTime? stopAt) => stopAt.HasValue && DateTime.Now >= stopAt.Value;

    private static string FormatSeconds(TimeSpan t)
    {
        var seconds = Math.Max(0, t.TotalSeconds);
        return seconds < 10 ? $"{seconds:0.0}s" : $"{Math.Ceiling(seconds):0}s";
    }

    /// <summary>
    /// Once <paramref name="remaining"/> drops to CountdownLabelThreshold (the countdown's final
    /// "in 0.1s" tick before the next action fires), returns <paramref name="label"/> ("Starting
    /// now"/"Clicking now"/"Spinning now") instead of <paramref name="countdownText"/> - purely a
    /// status-text display choice, never affects timing.
    /// </summary>
    private static string FormatCountdownStatus(string label, TimeSpan remaining, string countdownText)
    {
        return remaining <= CountdownLabelThreshold ? label : countdownText;
    }

    private void ReportStatus(string text, StatusKind kind) => StatusChanged?.Invoke(this, new StatusChangedEventArgs(text, kind));

    private void NotifyAutoStopped()
    {
        lock (_gate)
        {
            _cts = null;
            _loopTask = null;
            IsRunning = false;
        }

        AutoStopped?.Invoke(this, EventArgs.Empty);
    }
}
