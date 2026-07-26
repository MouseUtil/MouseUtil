using System.Runtime.InteropServices;

namespace MouseUtil.Interop;

/// <summary>
/// Values for ITaskbarList3.SetProgressState. Windows renders Paused as a solid amber/yellow fill
/// (and Error as red) with no extra styling needed on our end - that's why the "paused = yellow"
/// requirement needs no manual coloring, just this flag.
/// </summary>
[Flags]
internal enum TaskbarProgressState : uint
{
    NoProgress = 0x0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8
}

/// <summary>
/// COM entry point for the taskbar CLSID (CLSID_TaskbarList) - <c>new TaskbarInstance()</c> then cast
/// to <see cref="ITaskbarList3"/> is the standard way to obtain the interface without CoCreateInstance
/// boilerplate.
/// </summary>
[ComImport]
[Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
internal class TaskbarInstance
{
}

/// <summary>
/// Only declares the vtable slots up through SetProgressState (ITaskbarList/ITaskbarList2's members,
/// then the two ITaskbarList3 members this app actually calls) - COM interop only needs the interface
/// declared up to the last member it intends to invoke, since slots are resolved by declaration order,
/// not by name. The remaining ITaskbarList3 members (tab thumbnails, overlay icons, etc.) are simply
/// never exposed here.
/// </summary>
[ComImport]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);

    // ITaskbarList2
    void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ITaskbarList3 (only the two members this app needs)
    void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    void SetProgressState(IntPtr hwnd, TaskbarProgressState tbpFlags);
}
