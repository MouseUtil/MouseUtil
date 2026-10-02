using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseUtil.Interop;

/// <summary>
/// Sub-millisecond-accurate waiting for Auto Click's fire timing. Task.Delay is bounded by Windows'
/// default ~15.6ms system timer resolution, so at fast intervals its overshoot becomes the dominant
/// source of fire-time error. This waits out the bulk of the duration on a high-resolution waitable
/// timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10 1803+), then spins the last couple of
/// milliseconds off a Stopwatch, without touching the system-wide multimedia timer resolution that
/// timeBeginPeriod would affect.
/// </summary>
internal static class HighResolutionTimer
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    // Handed off to a Stopwatch spin instead of trusting the waitable timer for the very end - its due
    // time is itself blurred by clock-interrupt granularity, same as Task.Delay.
    private static readonly TimeSpan SpinMargin = TimeSpan.FromMilliseconds(2);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Blocks the calling thread for approximately <paramref name="duration"/>, returning early if
    /// <paramref name="token"/> is cancelled. For short (sub-second) waits only - callers on a thread
    /// that must stay responsive throughout should offload this to a background thread themselves.
    /// </summary>
    public static void Wait(TimeSpan duration, CancellationToken token)
    {
        if (duration <= TimeSpan.Zero || token.IsCancellationRequested)
        {
            return;
        }

        var coarseDuration = duration - SpinMargin;
        if (coarseDuration > TimeSpan.Zero)
        {
            WaitCoarse(coarseDuration, token);
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // Final stretch: spin rather than sleep/delay, since any further OS wait call re-introduces
        // the same clock-interrupt-granularity overshoot this method exists to avoid.
        var spinTarget = duration < SpinMargin ? duration : SpinMargin;
        var spinClock = Stopwatch.StartNew();
        while (spinClock.Elapsed < spinTarget && !token.IsCancellationRequested)
        {
            Thread.SpinWait(50);
        }
    }

    private static void WaitCoarse(TimeSpan duration, CancellationToken token)
    {
        var timerHandle = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (timerHandle == IntPtr.Zero)
        {
            // Pre-1803 Windows or otherwise unavailable - fall back to a plain wait rather than
            // spinning the whole duration away.
            Thread.Sleep((int)duration.TotalMilliseconds);
            return;
        }

        try
        {
            // Negative due time = relative delay, in 100ns units.
            var dueTime = -(long)(duration.TotalMilliseconds * 10_000);
            if (SetWaitableTimer(timerHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                var cancelHandle = token.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                WaitForMultipleObjects(2, new[] { timerHandle, cancelHandle }, false, INFINITE);
            }
        }
        finally
        {
            CloseHandle(timerHandle);
        }
    }
}
