using System.Runtime.InteropServices;
using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Registers a single system-wide hotkey (RegisterHotKey/WM_HOTKEY) so Start/Stop can be triggered
/// from anywhere, even while MouseUtil isn't the active window. WM_HOTKEY is delivered as an
/// ordinary message to whichever window registered it, so this subclasses the main window's WndProc
/// (SetWindowLongPtr/GWLP_WNDPROC) to observe that one message and otherwise forwards everything
/// unchanged to the original WndProc via CallWindowProc - the same technique WPF/Win32 apps use to
/// hook window messages that WinUI's own event surface doesn't expose.
///
/// This is also, pragmatically, the one place in the app that owns a WndProc subclass at all - other
/// features that need to observe a custom window message (e.g. SingleInstanceService's "show
/// yourself" message, see RegisterMessageHandler below) register a callback here instead of each
/// installing their own competing SetWindowLongPtr subclass, which would require carefully chaining
/// multiple _previousWndProc pointers for no real benefit.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // Arbitrary id private to this app's hotkey registration - RegisterHotKey scopes ids per-hWnd,
    // so this only needs to be unique among hotkeys this window itself registers (just the one).
    private const int HotkeyId = 0x4573;

    private IntPtr _hwnd;
    private IntPtr _previousWndProc;

    // Kept alive for the service's lifetime: Marshal.GetFunctionPointerForDelegate hands the OS a
    // raw function pointer into this delegate's thunk, which would otherwise be free to move/collect
    // once WndProc() (a static-ish method reference) stopped being reachable from managed code -
    // without this field the delegate could be GC'd while native code still holds a pointer to it.
    private NativeMethods.WndProc? _wndProcDelegate;

    private bool _isRegistered;

    // Callbacks for arbitrary custom window messages other than WM_HOTKEY (see RegisterMessageHandler)
    // - keyed by message id so multiple unrelated features can each observe their own message through
    // this single subclass without stepping on one another. Handlers receive the raw wParam/lParam so
    // features that need to inspect them (e.g. TrayIconService distinguishing left-click from
    // right-click on its callback message) can do so; simple parameterless handlers (e.g.
    // SingleInstanceService's "show yourself" message) use the Action overload below instead.
    private readonly Dictionary<uint, Action<IntPtr, IntPtr>> _messageHandlers = new();

    /// <summary>Raised on the UI thread (via the subclassed WndProc, already running on it) whenever the registered hotkey is pressed.</summary>
    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Subclasses <paramref name="hwnd"/>'s WndProc so this service can observe WM_HOTKEY. Must be
    /// called once, before the first TryRegister.
    /// </summary>
    public void AttachToWindow(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _wndProcDelegate = WndProc;
        var newWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _previousWndProc = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_WNDPROC, newWndProc);
    }

    /// <summary>
    /// Unregisters any previous hotkey, then attempts to register <paramref name="modifiers"/> +
    /// <paramref name="virtualKey"/>. Returns whether the new registration actually succeeded
    /// (RegisterHotKey fails if another app already owns that exact combination) - callers must
    /// check this and roll back to a previous known-good hotkey on failure, since this always
    /// unregisters first regardless of outcome.
    /// </summary>
    public bool TryRegister(uint modifiers, uint virtualKey)
    {
        if (_isRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _isRegistered = false;
        }

        _isRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        return _isRegistered;
    }

    /// <summary>
    /// Registers <paramref name="handler"/> to run whenever this window's subclassed WndProc observes
    /// <paramref name="message"/> - e.g. SingleInstanceService.ShowWindowMessageId, invoked when a
    /// second launch attempt signals this instance to come to the foreground. Runs on the UI thread,
    /// same as HotkeyPressed above, since it fires from the same WndProc. Must be called after
    /// AttachToWindow (there is no message to observe before the subclass is installed), though in
    /// practice the dictionary lookup itself is harmless either way - only the timing of messages
    /// actually arriving matters.
    /// </summary>
    public void RegisterMessageHandler(uint message, Action handler)
    {
        RegisterMessageHandler(message, (_, _) => handler());
    }

    /// <summary>
    /// Same as the Action overload above, but the handler also receives the message's raw wParam/lParam
    /// - needed by e.g. TrayIconService, whose single callback message carries the actual mouse event
    /// (left click vs. right click) in lParam.
    /// </summary>
    public void RegisterMessageHandler(uint message, Action<IntPtr, IntPtr> handler)
    {
        _messageHandlers[message] = handler;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        else if (_messageHandlers.TryGetValue(msg, out var handler))
        {
            handler(wParam, lParam);
        }

        return NativeMethods.CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _isRegistered = false;
        }
    }
}
