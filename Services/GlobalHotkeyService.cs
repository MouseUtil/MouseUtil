using System.Runtime.InteropServices;
using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Registers a single system-wide hotkey (RegisterHotKey/WM_HOTKEY) so Start/Stop can be triggered
/// from anywhere, even while MouseUtil isn't the active window. Subclasses the main window's WndProc
/// (SetWindowLongPtr/GWLP_WNDPROC) to observe WM_HOTKEY and forwards everything else unchanged via
/// CallWindowProc.
///
/// This is the app's one WndProc subclass - other features that need to observe a custom window
/// message (e.g. SingleInstanceService's "show yourself" message) register a callback here via
/// RegisterMessageHandler instead of installing a competing subclass.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // Arbitrary id, unique only among hotkeys this window registers (there's just the one).
    private const int HotkeyId = 0x4573;

    private IntPtr _hwnd;
    private IntPtr _previousWndProc;

    // Kept alive for the service's lifetime - without this field the delegate could be GC'd while
    // native code still holds a raw function pointer into it (via Marshal.GetFunctionPointerForDelegate).
    private NativeMethods.WndProc? _wndProcDelegate;

    private bool _isRegistered;

    // Callbacks for custom window messages other than WM_HOTKEY, keyed by message id so multiple
    // features can each observe their own message through this single subclass.
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
    /// <paramref name="virtualKey"/>. Returns whether registration succeeded (RegisterHotKey fails if
    /// another app already owns that combination) - callers must roll back on failure.
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
    /// Unregisters the current hotkey, if any, without registering a replacement. Used while the user
    /// is capturing a new hotkey in Settings - without this, pressing the current hotkey while
    /// recording would fire HotkeyPressed instead of reaching the key-capture handler.
    /// </summary>
    public void Unregister()
    {
        if (_isRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _isRegistered = false;
        }
    }

    /// <summary>
    /// Registers <paramref name="handler"/> to run whenever this window's subclassed WndProc observes
    /// <paramref name="message"/>. Runs on the UI thread, since it fires from the same WndProc. Must
    /// be called after AttachToWindow.
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
