using Microsoft.UI.Xaml;
using MouseUtil.Services;

namespace MouseUtil;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Enforces single-instance behavior before creating a window (see SingleInstanceService). If
    /// another instance is already running, signals it to come to the foreground and exits without
    /// creating a window; otherwise proceeds normally and holds the Mutex until this window closes.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!SingleInstanceService.TryAcquire())
        {
            SingleInstanceService.NotifyExistingInstance();
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow();

        // Skipped when LaunchWindowMode is "Tray" or "Minimized" (MainWindow's constructor already hid
        // or minimized the window and set the matching flag) - Activate() would otherwise flash the
        // window visible/restored before it settles into that state.
        if (_window is not MainWindow { StartHiddenInTray: true } && _window is not MainWindow { StartMinimized: true })
        {
            _window.Activate();
        }

        _window.Closed += (_, _) => SingleInstanceService.Release();
    }
}
