using System.Diagnostics;
using MouseUtil.Interop;

namespace MouseUtil.Services;

public enum AutomationMode
{
    Click,
    Jiggle
}

public enum StatusKind
{
    Off,
    Starting,
    Running,
    Imminent,
    Paused,

    /// <summary>
    /// The one-shot "Jiggling now" reported synchronously by Start() for Jiggle mode (no startup grace
    /// countdown). Distinct from Starting so MainWindow can apply a minimum on-screen hold to just this
    /// report, without affecting Click mode's real-time "Starting in Xs" countdown.
    /// </summary>
    JiggleStarting
}

public sealed class StatusChangedEventArgs : EventArgs
{
    public StatusChangedEventArgs(string text, StatusKind kind, double? progress = null, TimeSpan? remaining = null)
    {
        Text = text;
        Kind = kind;
        Progress = progress;
        Remaining = remaining;
    }

    public string Text { get; }
    public StatusKind Kind { get; }

    /// <summary>
    /// How much of the current countdown (startup grace, interval, or paused-resume) is left, from
    /// 1 down to 0 - drives MainWindow's taskbar progress bar, which drains as the countdown runs out.
    /// Null when this report doesn't represent movement through a countdown, leaving the bar untouched.
    /// </summary>
    public double? Progress { get; }

    /// <summary>
    /// Raw time remaining until the next action fires (or, while paused, until the resume countdown
    /// completes) - null when this report carries no live countdown. Added alongside the already-
    /// formatted Text/Progress so callers building their own display text don't re-derive seconds.
    /// </summary>
    public TimeSpan? Remaining { get; }
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
/// Runs the click/jiggle state machine on a background task. All timing decisions happen here;
/// callers only ever see <see cref="StatusChanged"/> notifications and must marshal them to the UI thread.
/// </summary>
public sealed class MouseAutomationEngine
{
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ImminentThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StillnessDisplayThreshold = TimeSpan.FromSeconds(5);
    private const int TickMilliseconds = 100;

    // Randomize-interval bounds (see GetEffectiveInterval): the randomized draw's floor is whichever
    // is larger, 10% of the configured interval or this absolute minimum - at short intervals, 10%
    // alone could fall below Jiggle mode's ~192ms blocking JiggleSweep animation, leaving no visible
    // gap between actions.
    private const double RandomizeIntervalMinPercent = 0.10;
    private static readonly TimeSpan RandomizeIntervalMinFloor = TimeSpan.FromMilliseconds(250);

    // How long the post-action caption ("Clicked"/"Jiggling now"/"Resuming now" - set at each fire
    // site in RunLoopAsync) is shown before the real countdown for the next action takes over. Measured
    // off intervalClock, the same clock driving the actual countdown, so this only ever changes
    // displayed text - it never adds real time between actions.
    private static readonly TimeSpan PostActionCaptionDuration = TimeSpan.FromMilliseconds(300);
    private const int JiggleRadiusPixels = 6; // ~12px diameter circle
    private const int JiggleSteps = 16;
    private const double JiggleStepDurationMs = 12.0;

    private static readonly TimeSpan FirstClickSelfStopWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _autoMoving;

    // UTC ticks of when this run's very first Click-mode action was injected, or 0 if not yet.
    // Stamped exactly once per run, before injecting the click (not after), so a self-inflicted stop
    // arriving almost instantly on the UI thread still sees it set. Behind Interlocked since it's
    // written from this engine's background loop thread and read from the UI thread.
    private long _firstClickInjectedTicks;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    /// <summary>Raised when the loop stops itself (scheduled stop time reached) rather than via Stop().</summary>
    public event EventHandler? AutoStopped;

    /// <summary>
    /// Raised every time FireAction completes, including the first action fired right after the
    /// startup grace period (or immediately when <see cref="Start"/>'s skipStartupCountdown is true).
    /// Fires from this engine's background loop thread - callers must marshal to the UI thread.
    /// </summary>
    public event EventHandler<ActionPerformedEventArgs>? ActionPerformed;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts the automation loop. skipStartupCountdown bypasses the usual StartupGracePeriod, used
    /// when Start is triggered via the global hotkey (performs the first action immediately). Jiggle
    /// mode always skips the countdown regardless of this flag.
    ///
    /// randomizeInterval, when true, draws a fresh random gap (see GetEffectiveInterval) at the start
    /// of every cycle instead of firing at a fixed interval. It never affects the pause-on-movement
    /// resume threshold (pauseOnMovementEnabled), which always waits the full configured interval since
    /// that's a direct reaction to the user's own mouse movement, not part of the automated cadence.
    /// </summary>
    public void Start(AutomationMode mode, TimeSpan interval, DateTime? stopAt, int? stopAfterActionCount, bool pauseOnMovementEnabled, bool randomizeInterval, bool skipStartupCountdown = false)
    {
        lock (_gate)
        {
            if (_cts != null)
            {
                return;
            }

            IsRunning = true;
            Interlocked.Exchange(ref _firstClickInjectedTicks, 0);

            // Jiggle mode skips RunStartupGraceAsync, so without this the first status report wouldn't
            // happen until the background task fires the first jiggle, leaving the UI showing stale
            // text briefly. Reporting synchronously here, before Task.Run, closes that gap - matching
            // every later jiggle, whose own caption is likewise reported before it fires (see
            // RunLoopAsync's ReportCaptionForFire), not after.
            if (mode == AutomationMode.Jiggle)
            {
                ReportStatus("Jiggling now", StatusKind.JiggleStarting, 1);
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loopTask = Task.Run(() => RunLoopAsync(mode, interval, stopAt, stopAfterActionCount, pauseOnMovementEnabled, randomizeInterval, skipStartupCountdown, cts.Token), cts.Token);
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

    private async Task RunLoopAsync(AutomationMode mode, TimeSpan interval, DateTime? stopAt, int? stopAfterActionCount, bool pauseOnMovementEnabled, bool randomizeInterval, bool skipStartupCountdown, CancellationToken token)
    {
        // Counts actions fired so far, checked against stopAfterActionCount right after each
        // FireAction call. Returns true if the caller should stop the loop.
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
            // Only Click mode started via the Start button needs the startup grace period, so the
            // click that started it isn't immediately consumed as the first auto-click/stop. Jiggle
            // mode and hotkey-triggered starts both skip it.
            var needsStartupGrace = !skipStartupCountdown && mode == AutomationMode.Click;
            if (needsStartupGrace && !await RunStartupGraceAsync(stopAt, token).ConfigureAwait(false))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // At or below a 1s configured interval, the countdown text would tick over too fast to
            // read, so this just shows a plain static verb the whole run instead - no countdown, no
            // post-action caption either. Based on the configured interval, not effectiveInterval,
            // which stays fixed for the whole run.
            var isSubSecondInterval = interval <= TimeSpan.FromSeconds(1);
            var verb = mode == AutomationMode.Click ? "Clicking" : "Jiggling";

            // The caption shown for PostActionCaptionDuration right after a normal (non-resume) fire.
            // "Clicked" for Click mode, since a click is instantaneous - there's nothing still
            // happening to call "now"; "Jiggling now" for Jiggle mode, since its sweep animation is a
            // real, visible, in-progress event.
            var normalCaption = mode == AutomationMode.Click ? "Clicked" : "Jiggling now";

            // Started before firing, not after, so that Jiggle mode's blocking JiggleSweep animation
            // (~192ms) counts against the first inter-action gap instead of adding invisible extra
            // time on top of it - see the main loop's intervalClock deadline below for the full reasoning.
            var intervalClock = Stopwatch.StartNew();
            var pauseClock = Stopwatch.StartNew();

            // The actual gap this cycle waits for - equal to interval when randomizeInterval is off,
            // or a fresh random draw (GetEffectiveInterval) otherwise. Redrawn on every
            // intervalClock.Restart(). Never used for the pause-on-movement stillness threshold, which
            // always compares against the raw configured interval to stay predictable.
            var effectiveInterval = GetEffectiveInterval(interval, randomizeInterval);

            // The caption TextOrCaption shows for PostActionCaptionDuration after a fire - "Clicked"/
            // "Jiggling now" after a normal fire, "Resuming now" after a pause-on-movement auto-resume.
            // Only ever changed by ReportCaptionForFire below, which sets and reports it together, so
            // there's a single place that decides "what just fired" instead of a separately-mutated
            // field callers have to keep in sync by hand. This initial value covers the very first
            // fire, before the while loop, which never calls ReportCaptionForFire itself - Click mode's
            // first "Clicked" already falls out of TextOrCaption's own check below (a click is instant,
            // so intervalClock.Elapsed is still ~0 on the first tick), and Jiggle mode's first "Jiggling
            // now" is Start()'s own separate one-shot StatusKind.JiggleStarting report.
            var currentCaption = normalCaption;

            // Below PostActionCaptionDuration since the last fire, returns currentCaption instead of
            // countdownText. Reads intervalClock directly rather than taking a snapshot, so it's purely
            // a display overlay on the already-running countdown clock - it never adds time between
            // actions (unlike a naive Task.Delay-based hold would).
            string TextOrCaption(string countdownText)
            {
                if (isSubSecondInterval)
                {
                    return verb;
                }

                return intervalClock.Elapsed < PostActionCaptionDuration ? currentCaption : countdownText;
            }

            // Sets and immediately reports caption for this fire, before FireAndCheckActionCountStop()
            // potentially blocks for Jiggle mode's ~192ms sweep animation. Nothing else reports status
            // during that block, so this is what stays on screen for its whole duration -
            // TextOrCaption's own intervalClock.Elapsed check then covers whatever's left of
            // PostActionCaptionDuration once the fire call returns. Skipped for sub-second intervals,
            // which never show a caption at all.
            void ReportCaptionForFire(string caption)
            {
                currentCaption = caption;

                if (isSubSecondInterval)
                {
                    return;
                }

                var firingKind = effectiveInterval <= ImminentThreshold ? StatusKind.Imminent : StatusKind.Running;
                ReportStatus(caption, firingKind, 1, effectiveInterval);
            }

            if (FireAndCheckActionCountStop())
            {
                return;
            }

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

                // Mode-scoping (Auto click vs Jiggle) happens on the caller side (see
                // SettingsPanel.IsPauseOnMovementActiveForMode) - pauseOnMovementEnabled arriving here
                // is already the fully-resolved bool.
                var pauseOnMovementActive = pauseOnMovementEnabled;
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
                        pauseClock.Restart();

                        // Reset the taskbar bar to full once, right as pausing begins, then freeze it -
                        // every other Paused report below omits progress (null) so it stays there.
                        ReportStatus("Paused", StatusKind.Paused, 1);
                    }
                    else
                    {
                        // Elapsed-since-pause off a monotonic Stopwatch rather than DateTime.UtcNow,
                        // so Task.Delay overshoot and mid-run system clock adjustments can't skew it.
                        var stillness = pauseClock.Elapsed;

                        if (stillness >= interval)
                        {
                            // Stood still for the full interval: fire immediately, then start a fresh
                            // full-length countdown - never resume from where it froze. Restarted before
                            // firing so a blocking Jiggle sweep counts against the new interval.
                            paused = false;
                            intervalClock.Restart();
                            effectiveInterval = GetEffectiveInterval(interval, randomizeInterval);
                            ReportCaptionForFire("Resuming now");
                            if (FireAndCheckActionCountStop())
                            {
                                return;
                            }

                            lastPos = NativeMethods.GetCursorPosition();
                            await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
                            continue;
                        }

                        if (stillness >= StillnessDisplayThreshold)
                        {
                            var resumeRemaining = interval - stillness;
                            ReportStatus(TextOrCaption($"Resuming in {FormatSeconds(resumeRemaining, showTenths: false)}"), StatusKind.Paused, remaining: resumeRemaining);
                        }
                        else
                        {
                            ReportStatus("Paused", StatusKind.Paused);
                        }
                    }

                    await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
                    continue;
                }

                // Deadline measured off a monotonic Stopwatch, not a nominal tick countdown, so
                // Task.Delay overshoot can never compound into drift, and a mid-run system clock
                // adjustment can't throw it off either.
                var remaining = effectiveInterval - intervalClock.Elapsed;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                var kind = remaining <= ImminentThreshold ? StatusKind.Imminent : StatusKind.Running;
                var progress = remaining.TotalMilliseconds / effectiveInterval.TotalMilliseconds;
                ReportStatus(TextOrCaption($"{verb} in {FormatSeconds(remaining, showTenths: false)}"), kind, progress, remaining);

                if (remaining > TimeSpan.Zero)
                {
                    if (remaining.TotalMilliseconds <= TickMilliseconds)
                    {
                        // Last stretch before firing - Task.Delay's own overshoot (bounded by Windows'
                        // ~15.6ms system timer resolution) would otherwise become the actual fire-time
                        // error at fast intervals, so only this leg gets the high-resolution wait.
                        HighResolutionTimer.Wait(remaining, token);
                    }
                    else
                    {
                        await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
                    }
                }

                if (intervalClock.Elapsed >= effectiveInterval)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    // Restarted before firing, not after: Jiggle mode's JiggleSweep blocks synchronously
                    // for ~192ms to animate the cursor. Restarting first anchors the Stopwatch's zero-
                    // point to the moment we decide to fire, so that blocking time counts against the
                    // next interval instead of stacking on top of this one.
                    intervalClock.Restart();
                    effectiveInterval = GetEffectiveInterval(interval, randomizeInterval);
                    ReportCaptionForFire(normalCaption);

                    if (FireAndCheckActionCountStop())
                    {
                        return;
                    }

                    lastPos = NativeMethods.GetCursorPosition();
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
        // Monotonic Stopwatch deadline, not a nominal tick countdown - see RunLoopAsync's
        // intervalClock for why.
        var graceClock = Stopwatch.StartNew();

        while (true)
        {
            var remaining = StartupGracePeriod - graceClock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (IsStopTimeReached(stopAt))
            {
                NotifyAutoStopped();
                return false;
            }

            var startupProgress = remaining.TotalMilliseconds / StartupGracePeriod.TotalMilliseconds;
            ReportStatus($"Starting in {FormatSeconds(remaining)}", StatusKind.Starting, startupProgress, remaining);

            var sleepMs = (int)Math.Min(TickMilliseconds, remaining.TotalMilliseconds);
            if (sleepMs > 0)
            {
                await Task.Delay(sleepMs, token).ConfigureAwait(false);
            }
        }
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
            JiggleSweep(token);
        }

        ActionPerformed?.Invoke(this, new ActionPerformedEventArgs(mode));
    }

    /// <summary>
    /// True only if this run's very first Click-mode action was injected within the last
    /// FirstClickSelfStopWindow - used solely to decide whether to show the "shrug" status text when
    /// that first click landed on and toggled off the Start/Stop button; never to block or reverse a
    /// stop. Since _firstClickInjectedTicks is stamped once per run, this can't return true for any
    /// later, self-inflicted stop.
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

    private void JiggleSweep(CancellationToken token)
    {
        var origin = NativeMethods.GetCursorPosition();
        _autoMoving = true;

        try
        {
            var stopwatch = Stopwatch.StartNew();

            for (var step = 1; step <= JiggleSteps; step++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var angle = 2 * Math.PI * step / JiggleSteps;
                var x = origin.X + (int)Math.Round(JiggleRadiusPixels * Math.Cos(angle));
                var y = origin.Y + (int)Math.Round(JiggleRadiusPixels * Math.Sin(angle));
                NativeMethods.MoveCursorTo(x, y);

                var targetElapsedMs = JiggleStepDurationMs * step;
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

    /// <summary>
    /// Returns <paramref name="interval"/> unchanged when <paramref name="randomize"/> is false.
    /// Otherwise draws a uniformly random value in [floor, interval], where floor is the larger of
    /// RandomizeIntervalMinPercent of interval or RandomizeIntervalMinFloor. If interval is already at
    /// or below that floor, it's returned as-is rather than risk drawing something larger than configured.
    /// </summary>
    private static TimeSpan GetEffectiveInterval(TimeSpan interval, bool randomize)
    {
        if (!randomize)
        {
            return interval;
        }

        var floorMs = Math.Max(RandomizeIntervalMinFloor.TotalMilliseconds, interval.TotalMilliseconds * RandomizeIntervalMinPercent);
        if (floorMs >= interval.TotalMilliseconds)
        {
            return interval;
        }

        var rangeMs = interval.TotalMilliseconds - floorMs;
        var drawnMs = floorMs + Random.Shared.NextDouble() * rangeMs;
        return TimeSpan.FromMilliseconds(drawnMs);
    }

    /// <summary>
    /// Formats a countdown for status text - plain "Ns"/"N.Ns" under a minute, "Nm Ns" up to an hour,
    /// "Nh Nm Ns" beyond, e.g. 90s -> "1m 30s", 3900s -> "1h 5m 0s". <paramref name="showTenths"/>
    /// gates the sub-10s "N.Ns" tenths digit - only the "Starting in Xs" caller passes true; the
    /// Clicking/Jiggling/Resuming callers pass false for a plain whole-second countdown.
    /// </summary>
    public static string FormatSeconds(TimeSpan t, bool showTenths = true)
    {
        var totalSeconds = Math.Max(0, t.TotalSeconds);
        if (totalSeconds < 60)
        {
            return showTenths && totalSeconds < 9.5 ? $"{totalSeconds:0.0}s" : $"{Math.Ceiling(totalSeconds):0}s";
        }

        var wholeSeconds = (long)Math.Ceiling(totalSeconds);
        var hours = wholeSeconds / 3600;
        var minutes = wholeSeconds % 3600 / 60;
        var seconds = wholeSeconds % 60;

        return hours > 0 ? $"{hours}h {minutes}m {seconds}s" : $"{minutes}m {seconds}s";
    }

    private void ReportStatus(string text, StatusKind kind, double? progress = null, TimeSpan? remaining = null) => StatusChanged?.Invoke(this, new StatusChangedEventArgs(text, kind, progress, remaining));

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
