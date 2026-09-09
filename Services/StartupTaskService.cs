using Windows.ApplicationModel;

namespace MouseUtil.Services;

/// <summary>
/// Thin wrapper around the "MouseUtilStartup" <see cref="StartupTask"/> declared in
/// Package.appxmanifest (windows.startupTask extension), which controls whether the app launches
/// itself (no automation) at Windows logon.
/// Deliberately not mirrored into AppConfig/ConfigService - the OS can flip this state on its own
/// (e.g. the user disables it from Windows Settings > Apps > Startup), so it's the single source of
/// truth and callers must re-query rather than cache.
/// </summary>
internal static class StartupTaskService
{
    private const string TaskId = "MouseUtilStartup";

    public static async Task<StartupTaskState> GetStateAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        return task.State;
    }

    public static async Task<StartupTaskState> EnableAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        return await task.RequestEnableAsync();
    }

    public static async Task DisableAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        task.Disable();
    }
}
