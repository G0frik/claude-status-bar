using System.IO;
using System.Text;

namespace ClaudeStatusBar;

public sealed record ProcessInfo(int Pid, int ParentPid, string ExeName);

/// <summary>A visible, unowned, titled top-level window. Lower ZOrder is closer to the top.</summary>
public sealed record WindowInfo(IntPtr Handle, int Pid, string Title, int ZOrder);

public interface IProcessTable
{
    ProcessInfo? Get(int pid);
}

public interface IWindowTable
{
    /// <summary>The process's windows, topmost first.</summary>
    IReadOnlyList<WindowInfo> ForProcess(int pid);
}

/// <summary>
/// Finds the window a Claude Code session runs in, starting from the session's process (recorded
/// by the hooks) and walking up its parents to the first one that owns a window: the VS Code /
/// Antigravity window for the extension, the Windows Terminal window for the CLI. When a process
/// owns several windows (several VS Code windows), the one whose title names the session's
/// project wins. This is the pure part; <see cref="SessionWindowFinder"/> feeds it live data.
/// </summary>
public static class SessionWindows
{
    public const int MaxDepth = 8;

    // Walking stops at the shell and system processes: their windows (the taskbar, the desktop)
    // are never a session's window.
    private static readonly HashSet<string> StopAt = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "sihost.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe",
        "userinit.exe", "smss.exe", "csrss.exe", "System",
    };

    /// <summary>
    /// Whether a recorded PID still belongs to a Claude Code session. A PID can be reused by an
    /// unrelated process after the session ends; that must never open a random window.
    /// </summary>
    public static bool IsSessionProcess(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        return name.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node", StringComparison.OrdinalIgnoreCase);
    }

    public static WindowInfo? Resolve(int pid, string project, IProcessTable processes, IWindowTable windows)
    {
        if (pid <= 0) return null;
        var process = processes.Get(pid);
        if (process is null || !IsSessionProcess(process.ExeName)) return null;

        var seen = new HashSet<int>();
        for (int depth = 0; process is not null && depth < MaxDepth && seen.Add(process.Pid); depth++)
        {
            if (StopAt.Contains(process.ExeName)) return null;
            var owned = windows.ForProcess(process.Pid);
            if (owned.Count > 0) return Choose(owned, project);
            if (process.ParentPid <= 0) return null;
            process = processes.Get(process.ParentPid);
        }
        return null;
    }

    private static WindowInfo Choose(IReadOnlyList<WindowInfo> windows, string project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            var named = windows.Where(w => w.Title.Contains(project, StringComparison.OrdinalIgnoreCase))
                               .MinBy(w => w.ZOrder);
            if (named is not null) return named;
        }
        return windows.MinBy(w => w.ZOrder)!;
    }
}

/// <summary>
/// Live lookups and window activation. Everything here runs only on a click or a notification,
/// never in the widget's poll, and takes a fresh snapshot each time (a few milliseconds), so a
/// window that moved, closed or was reopened is always found as it is now.
/// </summary>
public static class SessionWindowFinder
{
    public static IntPtr Find(ClaudeState session)
    {
        var window = SessionWindows.Resolve(session.Pid, session.Project, ToolhelpProcessTable.Take(), TopLevelWindows.Take());
        return window?.Handle ?? ConsoleWindowOf(session.Pid);
    }

    /// <summary>Bring the window to the front (restoring it if minimized); flash it if Windows refuses.</summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hwnd)) return true;

        var flash = new Native.FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = Native.FLASHW_ALL | Native.FLASHW_TIMERNOFG,
        };
        Native.FlashWindowEx(ref flash);
        return false;
    }

    /// <summary>Whether the session's window is the one in front (the user is looking at it).</summary>
    public static bool IsForeground(ClaudeState session)
    {
        var hwnd = Find(session);
        return hwnd != IntPtr.Zero && hwnd == Native.GetForegroundWindow();
    }

    // A CLI session in a classic console window: the window belongs to conhost, not to an
    // ancestor, so ask the console itself. Under Windows Terminal this returns a hidden pseudo
    // window, which is ignored (the ancestor walk already found Terminal).
    private static IntPtr ConsoleWindowOf(int pid)
    {
        if (pid <= 0) return IntPtr.Zero;
        var process = ToolhelpProcessTable.Take().Get(pid);
        if (process is null || !SessionWindows.IsSessionProcess(process.ExeName)) return IntPtr.Zero;
        if (!Native.AttachConsole((uint)pid)) return IntPtr.Zero;
        try
        {
            var hwnd = Native.GetConsoleWindow();
            return hwnd != IntPtr.Zero && Native.IsWindowVisible(hwnd) ? hwnd : IntPtr.Zero;
        }
        finally { Native.FreeConsole(); }
    }
}

/// <summary>All processes at one moment, from a Toolhelp snapshot.</summary>
public sealed class ToolhelpProcessTable : IProcessTable
{
    private readonly Dictionary<int, ProcessInfo> _byPid = new();

    public static ToolhelpProcessTable Take()
    {
        var table = new ToolhelpProcessTable();
        using var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid) return table;
        var entry = new Native.PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESSENTRY32W>() };
        for (bool ok = Native.Process32FirstW(snapshot, ref entry); ok; ok = Native.Process32NextW(snapshot, ref entry))
            table._byPid[(int)entry.th32ProcessID] = new ProcessInfo((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile);
        return table;
    }

    public ProcessInfo? Get(int pid) => _byPid.GetValueOrDefault(pid);
}

/// <summary>Visible, unowned, titled top-level windows at one moment, in z-order.</summary>
public sealed class TopLevelWindows : IWindowTable
{
    private readonly Dictionary<int, List<WindowInfo>> _byPid = new();

    public static TopLevelWindows Take()
    {
        var table = new TopLevelWindows();
        int z = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            z++;
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            int length = Native.GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!table._byPid.TryGetValue((int)pid, out var list)) table._byPid[(int)pid] = list = new();
            list.Add(new WindowInfo(hwnd, (int)pid, title.ToString(), z));
            return true;
        }, IntPtr.Zero);
        return table;
    }

    public IReadOnlyList<WindowInfo> ForProcess(int pid) =>
        _byPid.TryGetValue(pid, out var list) ? list : Array.Empty<WindowInfo>();
}
