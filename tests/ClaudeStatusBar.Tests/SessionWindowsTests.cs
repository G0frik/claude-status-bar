using Xunit;

namespace ClaudeStatusBar.Tests;

public class SessionWindowsTests
{
    private sealed class Processes(params ProcessInfo[] list) : IProcessTable
    {
        public ProcessInfo? Get(int pid) => list.FirstOrDefault(p => p.Pid == pid);
    }

    private sealed class Windows(params WindowInfo[] list) : IWindowTable
    {
        public IReadOnlyList<WindowInfo> ForProcess(int pid) => list.Where(w => w.Pid == pid).OrderBy(w => w.ZOrder).ToList();
    }

    private static WindowInfo Win(int handle, int pid, string title, int z) => new(handle, pid, title, z);

    // The real chain observed with the VS Code extension in Antigravity.
    private static readonly Processes Antigravity = new(
        new(14944, 16340, "claude.exe"),
        new(16340, 23160, "Antigravity IDE.exe"),  // extension host, no windows
        new(23160, 7552, "Antigravity IDE.exe"),   // main process, owns the windows
        new(7552, 7000, "explorer.exe"));

    [Fact]
    public void Walks_up_from_the_extension_to_the_editor_window()
    {
        var windows = new Windows(Win(1, 23160, "claude-status-bar - Antigravity IDE", 5), Win(9, 7552, "Program Manager", 90));
        Assert.Equal((IntPtr)1, SessionWindows.Resolve(14944, "claude-status-bar", Antigravity, windows)!.Handle);
    }

    [Fact]
    public void With_several_editor_windows_the_one_naming_the_project_wins()
    {
        var windows = new Windows(
            Win(1, 23160, "notes - Antigravity IDE", 1),                 // topmost, other project
            Win(2, 23160, "claude-status-bar - Antigravity IDE", 7),
            Win(3, 23160, "api - Antigravity IDE", 3));
        Assert.Equal((IntPtr)2, SessionWindows.Resolve(14944, "claude-status-bar", Antigravity, windows)!.Handle);
        Assert.Equal((IntPtr)2, SessionWindows.Resolve(14944, "CLAUDE-STATUS-BAR", Antigravity, windows)!.Handle);
    }

    [Fact]
    public void Without_a_title_match_the_topmost_window_of_that_process_wins()
    {
        var windows = new Windows(Win(1, 23160, "notes - Antigravity IDE", 4), Win(2, 23160, "api - Antigravity IDE", 2));
        Assert.Equal((IntPtr)2, SessionWindows.Resolve(14944, "unrelated", Antigravity, windows)!.Handle);
        Assert.Equal((IntPtr)2, SessionWindows.Resolve(14944, "", Antigravity, windows)!.Handle);
    }

    [Fact]
    public void CLI_in_Windows_Terminal_resolves_to_the_terminal_window()
    {
        var processes = new Processes(
            new(500, 400, "claude.exe"),
            new(400, 300, "pwsh.exe"),
            new(300, 7552, "WindowsTerminal.exe"),
            new(7552, 7000, "explorer.exe"));
        var windows = new Windows(Win(7, 300, "PowerShell", 3));
        Assert.Equal((IntPtr)7, SessionWindows.Resolve(500, "my-api", processes, windows)!.Handle);
    }

    [Fact]
    public void Node_based_CLI_counts_as_a_session_process()
    {
        var processes = new Processes(new(10, 20, "node.exe"), new(20, 1, "WindowsTerminal.exe"));
        Assert.NotNull(SessionWindows.Resolve(10, "", processes, new Windows(Win(4, 20, "Terminal", 1))));
    }

    [Fact]
    public void Never_resolves_to_the_shell_or_desktop()
    {
        // A classic console session: the window belongs to conhost, not to an ancestor. The walk
        // must stop at explorer instead of returning the desktop window ("Program Manager").
        var processes = new Processes(new(500, 400, "claude.exe"), new(400, 7552, "cmd.exe"), new(7552, 7000, "explorer.exe"));
        var windows = new Windows(Win(9, 7552, "Program Manager", 90));
        Assert.Null(SessionWindows.Resolve(500, "", processes, windows));
    }

    [Fact]
    public void Dead_or_reused_pids_resolve_to_nothing()
    {
        var windows = new Windows(Win(1, 23160, "claude-status-bar - Antigravity IDE", 1));
        Assert.Null(SessionWindows.Resolve(99999, "x", Antigravity, windows));          // not running
        Assert.Null(SessionWindows.Resolve(0, "x", Antigravity, windows));              // never recorded

        // The session ended and its PID now belongs to an unrelated program with a window.
        var reused = new Processes(new ProcessInfo(14944, 1, "notepad.exe"));
        Assert.Null(SessionWindows.Resolve(14944, "x", reused, new Windows(Win(5, 14944, "Untitled - Notepad", 1))));
    }

    [Fact]
    public void Parent_cycles_and_deep_chains_end_without_a_window()
    {
        var cycle = new Processes(new(1, 2, "claude.exe"), new(2, 3, "a.exe"), new(3, 1, "b.exe"));
        Assert.Null(SessionWindows.Resolve(1, "", cycle, new Windows([])));

        // A window further up than MaxDepth isn't reached.
        var chain = Enumerable.Range(1, SessionWindows.MaxDepth + 2)
            .Select(i => new ProcessInfo(i, i + 1, i == 1 ? "claude.exe" : $"p{i}.exe")).ToArray();
        var far = new Windows(Win(1, SessionWindows.MaxDepth + 2, "far", 1));
        Assert.Null(SessionWindows.Resolve(1, "", new Processes(chain), far));
    }

    [Theory]
    [InlineData("claude.exe", true)]
    [InlineData("Claude.EXE", true)]
    [InlineData("claude", true)]
    [InlineData("node.exe", true)]
    [InlineData("nodejs.exe", false)]
    [InlineData("notepad.exe", false)]
    [InlineData("", false)]
    public void Recognizes_session_processes(string exe, bool expected) =>
        Assert.Equal(expected, SessionWindows.IsSessionProcess(exe));
}

public class SessionWindowLiveTests
{
    [Fact]
    public void Process_snapshot_sees_this_process_and_its_parent_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var table = ToolhelpProcessTable.Take();
        var windows = TopLevelWindows.Take();
        watch.Stop();

        var self = table.Get(Environment.ProcessId);
        Assert.NotNull(self);
        Assert.NotNull(table.Get(self!.ParentPid));
        Assert.True(watch.ElapsedMilliseconds < 250, $"snapshot took {watch.ElapsedMilliseconds} ms");
        _ = windows.ForProcess(Environment.ProcessId); // no window here; just must not throw
    }

    [Fact]
    public void Unknown_session_has_no_window_and_activation_refuses_a_null_handle()
    {
        Assert.Equal(IntPtr.Zero, SessionWindowFinder.Find(new ClaudeState { Pid = 0 }));
        Assert.False(SessionWindowFinder.Activate(IntPtr.Zero));
    }
}
