using System.Diagnostics;
using System.IO;

namespace ClaudeStatusBar;

/// <summary>
/// After a click has brought a session's window to the front, switches that window to the
/// session's chat tab when it's safe: the window really is in front (the editor delivers a URI
/// to its last focused window, and a URI landing in another window would start a second copy of
/// the session there), the session runs in the extension's panel, and the extension has the
/// /open route, and the session has a conversation (the extension answers a link to a session it
/// doesn't know by starting a new one). Anything else leaves things as they were: the window in front.
/// </summary>
public static class ChatTabOpener
{
    // A double click, or a notification click right after a dot click, starts the editor once.
    private static readonly Debounce Recent = new(TimeSpan.FromSeconds(2), () => DateTime.UtcNow);

    // A window being restored from minimized becomes the foreground window a little after
    // SetForegroundWindow returns; wait for it rather than skip.
    private static readonly TimeSpan ForegroundWait = TimeSpan.FromMilliseconds(500);

    /// <param name="activated">Whether SetForegroundWindow accepted the session's window.</param>
    public static void Open(ClaudeState session, SessionChain? chain, bool activated)
    {
        var id = session.SessionId;
        if (SessionStore.HasConversation(session) != true) { OpenLog.Write($"{id} skip:no-conversation"); return; }
        if (chain is null) { OpenLog.Write($"{id} skip:no-chain"); return; }
        if (!activated) { OpenLog.Write($"{id} skip:not-in-front (refused)"); return; }
        if (!Recent.TryEnter(id)) { OpenLog.Write($"{id} skip:repeat"); return; }

        // Waiting for the foreground, reading extension.js and starting a process stay off the UI thread.
        Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            var hwnd = chain.Window.Handle;
            while (Native.GetForegroundWindow() != hwnd && watch.Elapsed < ForegroundWait)
                await Task.Delay(25);
            var front = Native.GetForegroundWindow();
            if (front != hwnd)
            {
                Native.GetWindowThreadProcessId(front, out uint frontPid);
                var who = frontPid == chain.Window.Pid ? "another window of the editor" : "another app";
                OpenLog.Write($"{id} skip:not-in-front ({who} after {watch.ElapsedMilliseconds} ms)");
                return;
            }
            long waited = watch.ElapsedMilliseconds;

            var (tab, reason) = ChatTabLink.Plan(id,
                Native.ImagePath(chain.SessionPid), Native.ImagePath(chain.ParentPid), Native.ImagePath(chain.Window.Pid),
                InstalledEditorFiles.Instance);
            if (tab is null) { OpenLog.Write($"{id} skip:{reason} ({watch.ElapsedMilliseconds} ms)"); return; }
            try
            {
                EditorLauncher.Start(tab);
                OpenLog.Write($"{id} launched {tab.Uri[..tab.Uri.IndexOf(':')]} {tab.Extension} ({watch.ElapsedMilliseconds} ms, {waited} ms for the foreground)");
            }
            catch (Exception e) { OpenLog.Write($"{id} failed: {e.GetType().Name}: {e.Message}"); }
        });
    }
}

/// <summary>
/// Hands a link to the editor the way the editor registers itself for its URL scheme
/// (<c>&lt;exe&gt; --open-url -- &lt;uri&gt;</c>), but starting the exe that runs the session's
/// window rather than whichever install the registry names. The new process passes the URI to the
/// running editor and exits.
/// </summary>
public static class EditorLauncher
{
    /// <summary>
    /// Variables the widget inherits from the Claude Code session that started it, which the
    /// editor must not get: ELECTRON_RUN_AS_NODE=1 would run the editor exe as plain Node (it then
    /// fails with "bad option: --open-url"), VSCODE_* point at that session's editor internals,
    /// and CLAUDE_CODE_* include the session's messaging token. CLAUDE_CONFIG_DIR is the user's own
    /// setting and stays.
    /// </summary>
    public static bool IsSessionVariable(string name) =>
        name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLAUDE_AGENT_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase);

    /// <summary>No shell: the URI is one argument of a list, so nothing in it is ever parsed as a command.</summary>
    public static ProcessStartInfo StartInfo(ChatTab tab)
    {
        var info = new ProcessStartInfo(tab.EditorExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(tab.EditorExe) ?? "",
        };
        info.ArgumentList.Add("--open-url");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(tab.Uri);
        foreach (var name in info.Environment.Keys.Where(IsSessionVariable).ToList())
            info.Environment.Remove(name);
        return info;
    }

    public static void Start(ChatTab tab)
    {
        using var process = Process.Start(StartInfo(tab));
    }
}

/// <summary>Lets a key through at most once per interval.</summary>
public sealed class Debounce(TimeSpan interval, Func<DateTime> now)
{
    private readonly Dictionary<string, DateTime> _last = new();

    public bool TryEnter(string key)
    {
        lock (_last)
        {
            var t = now();
            foreach (var stale in _last.Where(kv => t - kv.Value >= interval).Select(kv => kv.Key).ToList())
                _last.Remove(stale);
            if (_last.ContainsKey(key)) return false;
            _last[key] = t;
            return true;
        }
    }
}

/// <summary>
/// One line per click that tried to open a chat tab: the session id and what was decided. Never
/// window titles. Written next to the executable as open.log.
/// </summary>
public static class OpenLog
{
    private static readonly CappedLog Log = new("open.log");

    public static void Write(string line) => Log.Write(line);
}
