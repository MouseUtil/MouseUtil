using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Enforces a single running instance of MouseUtil. The first process to launch acquires a named
/// Mutex and holds it for its entire lifetime; any later launch detects the Mutex is already owned,
/// forwards a "come to the foreground" signal to the first instance's window, and exits without ever
/// constructing a window of its own.
///
/// The signal is a custom message minted via RegisterWindowMessage (no shared memory or other IPC
/// needed) observed through GlobalHotkeyService's existing WndProc subclass, the same mechanism it
/// uses for WM_HOTKEY, rather than installing a second, competing subclass.
/// </summary>
internal static class SingleInstanceService
{
    // Fixed, unique name scoped to the current user session (no "Global\" prefix). Debug builds get
    // a distinct name/title so a Debug build can run side by side with an installed Release build.
#if DEBUG
    private const string MutexName = "MouseUtil-SingleInstance-3f1b7c2e-6c8b-4b96-9c10-8b2a6e9e9b54-Debug";
    private const string ShowWindowMessageName = "MouseUtil-ShowInstance-3f1b7c2e-6c8b-4b96-9c10-8b2a6e9e9b54-Debug";
#else
    private const string MutexName = "MouseUtil-SingleInstance-3f1b7c2e-6c8b-4b96-9c10-8b2a6e9e9b54";
    private const string ShowWindowMessageName = "MouseUtil-ShowInstance-3f1b7c2e-6c8b-4b96-9c10-8b2a6e9e9b54";
#endif

    // Used by FindWindow to locate the first instance's window by title (set as MainWindow's own
    // Window.Title). FindWindow enumerates top-level windows regardless of visibility, so this keeps
    // working whether that window is normal, minimized, or hidden.
#if DEBUG
    internal const string MainWindowTitle = "MouseUtil Debug";
#else
    internal const string MainWindowTitle = "MouseUtil";
#endif

    private static Mutex? _mutex;

    /// <summary>
    /// Numeric id of the custom "show yourself" message, resolved once per process via
    /// RegisterWindowMessage. Used by the first instance to register a handler and by any later
    /// instance to post it.
    /// </summary>
    public static uint ShowWindowMessageId { get; } = NativeMethods.RegisterWindowMessage(ShowWindowMessageName);

    /// <summary>
    /// Attempts to become the single running instance. Returns true if this is the first instance
    /// (the Mutex is now held and must eventually be released via <see cref="Release"/>), or false if
    /// another instance already owns it, in which case call <see cref="NotifyExistingInstance"/> instead.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>
    /// Called only when TryAcquire() returned false. Finds the first instance's window by its fixed
    /// title and posts it the show-window message, then drops this process's (non-owning) handle to
    /// the Mutex. Does nothing but the cleanup if the window can't be found.
    /// </summary>
    public static void NotifyExistingInstance()
    {
        var hwnd = NativeMethods.FindWindow(null, MainWindowTitle);
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(hwnd, ShowWindowMessageId, IntPtr.Zero, IntPtr.Zero);
        }

        // This process never owned the Mutex, so just close our handle to it.
        _mutex?.Dispose();
    }

    /// <summary>
    /// Releases the Mutex this (first) instance has held since a successful TryAcquire(). Must only
    /// be called by the owning instance, at real process shutdown.
    /// </summary>
    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
