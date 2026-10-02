using System.Runtime.InteropServices;
using MouseUtil.Interop;

namespace MouseUtil.Services;

/// <summary>
/// Wraps ITaskbarList3 to show the running interval countdown as a progress bar overlaid on this
/// app's taskbar icon - the same mechanism installers use. Optional/cosmetic (see
/// AppConfig.ShowTaskbarProgress); every member no-ops if the COM object couldn't be created.
///
/// Must only be called from the UI thread - the underlying COM object is created single-threaded
/// on whichever thread first touches it (MainWindow's constructor).
/// </summary>
public sealed class TaskbarProgressService : IDisposable
{
    private const ulong ProgressResolution = 1000;

    private IntPtr _hwnd;
    private ITaskbarList3? _taskbarList;

    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;

        try
        {
            _taskbarList = (ITaskbarList3)new TaskbarInstance();
            _taskbarList.HrInit();
        }
        catch (COMException)
        {
            _taskbarList = null;
        }
    }

    /// <summary>
    /// Sets the taskbar icon's progress fill to <paramref name="fraction"/> (0-1, clamped). Windows
    /// renders <paramref name="paused"/> as a solid amber/yellow fill automatically (TBPF_PAUSED) -
    /// see TaskbarProgressState's doc comment - so no manual color handling is needed here.
    /// </summary>
    public void SetProgress(double fraction, bool paused)
    {
        if (_taskbarList == null)
        {
            return;
        }

        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        _taskbarList.SetProgressState(_hwnd, paused ? TaskbarProgressState.Paused : TaskbarProgressState.Normal);
        _taskbarList.SetProgressValue(_hwnd, (ulong)(clamped * ProgressResolution), ProgressResolution);
    }

    /// <summary>Removes the progress overlay entirely - called on stop and whenever the setting is turned off.</summary>
    public void Clear()
    {
        _taskbarList?.SetProgressState(_hwnd, TaskbarProgressState.NoProgress);
    }

    public void Dispose()
    {
        if (_taskbarList != null)
        {
            Marshal.FinalReleaseComObject(_taskbarList);
            _taskbarList = null;
        }
    }
}
