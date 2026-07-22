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
    /// Enforces single-instance behavior before anything else happens (see SingleInstanceService).
    /// If another instance is already running, this process never constructs a window at all - it
    /// just signals the existing instance to come to the foreground and exits immediately. Otherwise
    /// this is the first (and only) instance: startup proceeds as normal, and the Mutex acquired by
    /// TryAcquire() is held until the window actually closes, i.e. real app exit.
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
        _window.Activate();
        _window.Closed += (_, _) => SingleInstanceService.Release();
    }
}
