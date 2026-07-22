using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Enforces a single running instance of MouseUtil. The first process to launch acquires a named
/// Mutex and keeps it held for its entire lifetime (see TryAcquire/Release); any later launch
/// attempt detects the Mutex is already owned (TryAcquire returns false), forwards a "please come to
/// the foreground" signal to the first instance's window (see NotifyExistingInstance), and exits
/// immediately without ever constructing a window of its own - the caller (App.OnLaunched) is
/// responsible for skipping window creation entirely in that case.
///
/// The forwarded signal is a custom message minted via RegisterWindowMessage, which resolves to the
/// same numeric id in every process that registers the same string - no shared memory or other IPC
/// is needed beyond that. The first instance observes it through GlobalHotkeyService's existing
/// WndProc subclass (see MainWindow.InitializeGlobalHotkey), the same mechanism it already uses for
/// WM_HOTKEY, rather than installing a second, competing subclass.
/// </summary>
internal static class SingleInstanceService
{
    // Fixed, unique name so this Mutex can never collide with an unrelated app's - scoped to the
    // current user session (no "Global\" prefix), which is all a single-user desktop utility needs.
    private const string MutexName = "MouseUtil-SingleInstance-3f1b7c2e-6c8b-4b96-9c10-8b2a6e9e9b54";

    private const string ShowWindowMessageName = "MouseUtil-ShowInstance-3f1b7c2e-6c8b-4b96-9c10-8b2a6e9e9b54";

    // Used by FindWindow to locate the first instance's top-level window from a second-instance
    // process that otherwise has no handle to it at all. Window.Title (set once in MainWindow's
    // constructor) is propagated straight through to the underlying HWND's window text, so searching
    // by title alone - no class name, no other implementation-detail assumptions about how WinAppSDK
    // registers its window class - is a simple, reliable way to find it. FindWindow enumerates
    // top-level windows regardless of visibility, so this keeps working whether that window is
    // currently normal, minimized, or hidden (e.g. a future tray-icon "hide instead of close" feature).
    private const string MainWindowTitle = "MouseUtil";

    private static Mutex? _mutex;

    /// <summary>
    /// Numeric id of the custom "show yourself" message. Resolved once per process (RegisterWindowMessage
    /// is idempotent/cheap and guaranteed to return the same value for the same string within a
    /// session), and used identically by both the first instance (to register a handler for it) and
    /// any later instance (to post it).
    /// </summary>
    public static uint ShowWindowMessageId { get; } = NativeMethods.RegisterWindowMessage(ShowWindowMessageName);

    /// <summary>
    /// Attempts to become the single running instance. Returns true if this is the first instance -
    /// the Mutex is now held by this process and stored for the lifetime of TryAcquire's caller (via
    /// the static field), and must eventually be released with <see cref="Release"/>. Returns false if
    /// another instance already owns it, in which case this process holds no Mutex ownership at all
    /// and must call <see cref="NotifyExistingInstance"/> instead of creating any UI.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>
    /// Called only when TryAcquire() returned false. Finds the first instance's window by its fixed
    /// title and posts it the registered show-window message, then drops this process's (non-owning)
    /// handle to the Mutex. Does nothing but the Mutex cleanup if the window can't be found (e.g. it
    /// closed in the narrow window between the Mutex check and this call) - there is no fallback
    /// window to create, since a second instance must never show UI of its own.
    /// </summary>
    public static void NotifyExistingInstance()
    {
        var hwnd = NativeMethods.FindWindow(null, MainWindowTitle);
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(hwnd, ShowWindowMessageId, IntPtr.Zero, IntPtr.Zero);
        }

        // TryAcquire returned false, so this process never actually owned the Mutex - nothing to
        // release, just close our handle to it.
        _mutex?.Dispose();
    }

    /// <summary>
    /// Releases the Mutex this (first) instance has held since a successful TryAcquire(). Must only
    /// be called by the instance that owns it, at real process shutdown (see App.OnLaunched's
    /// Window.Closed subscription) - never from the NotifyExistingInstance/second-instance path.
    /// </summary>
    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
