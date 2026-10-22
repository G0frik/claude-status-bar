using System.Runtime.InteropServices;

namespace ClaudeStatusBar;

/// <summary>
/// Win32 interop for tray geometry, monitor enumeration, z-order, and window styles.
/// Rectangles returned by the shell APIs are in physical pixels; callers convert to DIPs.
/// </summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // ----- tray -----

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// Bounds of the tray (clock + notification) cluster, via Shell_TrayWnd → TrayNotifyWnd.
    /// Returns null when the layout cannot be resolved.
    /// </summary>
    public static RECT? GetTrayRect()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return null;
        return GetWindowRect(notify, out var rc) ? rc : null;
    }

    // ----- monitors -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 0x1;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    public sealed record MonitorDesc(string Device, bool Primary, RECT Monitor, RECT Work);

    public static List<MonitorDesc> GetMonitors()
    {
        var list = new List<MonitorDesc>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
                list.Add(new MonitorDesc(mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, mi.rcMonitor, mi.rcWork));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ----- z-order -----

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ----- window styles -----

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ----- processes (Toolhelp snapshot) -----

    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    public sealed class SnapshotHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SnapshotHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32FirstW(SnapshotHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32NextW(SnapshotHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ----- process image paths (which editor install and which extension a session runs in) -----

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder exeName, ref uint size);

    /// <summary>Full path of a process's exe, or null if the process is gone or can't be queried.</summary>
    public static string? ImagePath(int pid)
    {
        if (pid <= 0) return null;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new System.Text.StringBuilder(1024);
            uint size = (uint)path.Capacity;
            return QueryFullProcessImageNameW(process, 0, path, ref size) ? path.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(process); }
    }

    private const int ERROR_INVALID_PARAMETER = 87; // OpenProcess: no process has this id
    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    /// <summary>
    /// Whether the process with this id is gone: none has the id, or only an exited one that a
    /// handle somewhere keeps around. False when it can't be told (e.g. access denied).
    /// </summary>
    public static bool HasExited(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero) return Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER;
        try { return GetExitCodeProcess(process, out uint code) && code != STILL_ACTIVE; }
        finally { CloseHandle(process); }
    }

    // ----- top-level windows -----

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    public const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags, uCount, dwTimeout;
    }

    public const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    public static extern bool FlashWindowEx(ref FLASHWINFO info);

    // ----- notifications -----

    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS; every other state (fullscreen app, presentation mode, quiet hours) means "don't".</summary>
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Whether Windows says not to disturb the user right now.</summary>
    public static bool UserBusy() =>
        SHQueryUserNotificationState(out int state) == 0 && state != QUNS_ACCEPTS_NOTIFICATIONS;

    // ----- consoles (classic conhost windows aren't owned by an ancestor of the session) -----

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();
}
