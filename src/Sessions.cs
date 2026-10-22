using System.IO;

namespace ClaudeStatusBar;

/// <summary>
/// Reads every session's status from sessions.d (one JSON file per session, written by the
/// hooks), re-parsing a file only when it changes. Sessions are returned in the order they were
/// first seen, so their dots keep their places. Each entry's SessionId is its file name.
///
/// Empty files (written by hooks older than per-session status) are skipped; when nothing
/// parseable remains, callers fall back to state.json. A session whose recorded process is gone
/// has its file deleted. Idle, finished or background-waiting sessions with no event for
/// <see cref="LeftoverSeconds"/> are hidden: leftovers of crashed sessions that recorded no process.
///
/// Idle sessions that have never had a conversation (no transcript yet) are hidden too. The
/// editor extension starts spare sessions that no chat tab shows, and there is nothing to open in
/// one: the extension answers a link to an unknown session by starting a new one. A session
/// shows up with its first prompt.
/// </summary>
public sealed class SessionStore
{
    public const double LeftoverSeconds = 12 * 3600;

    private readonly string _dir;
    private readonly Func<double> _now;
    private readonly Func<int, bool> _gone;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _order;

    private sealed record Entry(DateTime LastWrite, ClaudeState State, long Order);

    /// <param name="dir">Status-bar data directory (sessions.d is inside); defaults to <see cref="StateStore.Dir"/>.</param>
    /// <param name="now">Clock in unix seconds; defaults to the system clock.</param>
    /// <param name="gone">Whether a session's recorded process is gone; defaults to <see cref="ProcessGone"/>.</param>
    public SessionStore(string? dir = null, Func<double>? now = null, Func<int, bool>? gone = null)
    {
        _dir = Path.Combine(dir ?? StateStore.Dir, "sessions.d");
        _now = now ?? StateStore.UnixNow;
        _gone = gone ?? ProcessGone;
    }

    /// <summary>The session's Claude Code process has exited, or its id now belongs to another program.</summary>
    public static bool ProcessGone(int pid) =>
        Native.HasExited(pid) || Native.ImagePath(pid) is { } exe && !SessionWindows.IsSessionProcess(exe);

    /// <summary>Whether the last poll found any session status, shown or hidden.</summary>
    public bool AnyTracked { get; private set; }

    /// <summary>
    /// Whether the session has a conversation: true once its transcript exists, false while it
    /// doesn't, null when its transcript isn't known (a status written by older hooks).
    /// </summary>
    public static bool? HasConversation(ClaudeState s) =>
        string.IsNullOrEmpty(s.Transcript) ? null : File.Exists(s.Transcript);

    public IReadOnlyList<ClaudeState> Poll()
    {
        string[] files;
        try { files = Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []; }
        catch { files = []; }

        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _cache.Keys.Where(k => !present.Contains(k)).ToList())
            _cache.Remove(gone);

        foreach (var file in files)
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(file); }
            catch { continue; }

            _cache.TryGetValue(file, out var cached);
            if (cached is not null && cached.LastWrite == lastWrite) continue;

            // Null on an empty, partial or unreadable file: keep what we had and retry next poll.
            var state = Read(file);
            if (state is not null) _cache[file] = new Entry(lastWrite, state, cached?.Order ?? _order++);
        }

        // A crash, a killed terminal, or a hook that finished after SessionEnd leaves the file of a
        // session that no longer runs. Remove it, as SessionEnd would have, so the widget can quit.
        foreach (var (file, _) in _cache.Where(kv => kv.Value.State.Pid > 0 && _gone(kv.Value.State.Pid)).ToList())
        {
            try { File.Delete(file); } catch { }
            _cache.Remove(file);
        }

        AnyTracked = _cache.Count > 0;
        double now = _now();
        return _cache.Values
            .OrderBy(e => e.Order)
            .Select(e => StateStore.Effective(e.State, now))
            .Where(s => !(s.State is "idle" or "done" or "background" && now - s.Ts > LeftoverSeconds))
            .Where(s => !(s.State == "idle" && HasConversation(s) == false))
            .ToList();
    }

    private static ClaudeState? Read(string file)
    {
        try
        {
            var text = SharedFile.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var state = StateStore.Parse(text);
            state.SessionId = Path.GetFileName(file);
            return state;
        }
        catch { return null; }
    }
}

/// <summary>
/// Chooses the session the main icon, label and timer follow: the one that most needs attention
/// (awaiting permission, then working, then waiting on background tasks, then just finished,
/// then idle). Focus is sticky: the
/// current session keeps it while it is still in the highest class present, so a session that
/// starts working later doesn't take over the display.
/// </summary>
public static class SessionFocus
{
    public static int Rank(string state) => state switch
    {
        "permission" => 4,
        "thinking" or "tool" => 3,
        "background" => 2,
        "done" => 1,
        _ => 0,
    };

    public static ClaudeState? Pick(IReadOnlyList<ClaudeState> sessions, string? currentId)
    {
        if (sessions.Count == 0) return null;
        int top = sessions.Max(s => Rank(s.State));
        var current = sessions.FirstOrDefault(s => s.SessionId == currentId);
        if (current is not null && Rank(current.State) == top) return current;
        return sessions.Where(s => Rank(s.State) == top).MaxBy(s => s.Ts);
    }
}

/// <summary>Display names for sessions: the project name, numbered when several share it.</summary>
public static class SessionNames
{
    public static Dictionary<string, string> Build(IReadOnlyList<ClaudeState> sessions)
    {
        var names = new Dictionary<string, string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            var name = string.IsNullOrWhiteSpace(s.Project) ? "Claude" : s.Project;
            int n = counts[name] = counts.GetValueOrDefault(name) + 1;
            names[s.SessionId] = n == 1 ? name : $"{name} · {n}";
        }
        return names;
    }
}

/// <summary>How a session's status and durations read in the panel and notifications.</summary>
public static class SessionText
{
    public static string Status(ClaudeState s) => s.State switch
    {
        "thinking" => "Thinking…",
        "tool" => string.IsNullOrEmpty(s.Label) ? "Working…" : s.Label,
        "permission" => string.IsNullOrEmpty(s.Detail) ? "Awaiting permission" : $"Awaiting permission · {PermissionSubject(s.Detail)}",
        "background" => string.IsNullOrEmpty(s.Label) ? "Background tasks" : s.Label,
        "done" => "Done",
        _ => "Idle",
    };

    /// <summary>
    /// What a permission request is for, short: the tool name as recorded, or the tool taken
    /// from a notification message ("Claude needs your permission to use Bash" → "Bash").
    /// </summary>
    public static string PermissionSubject(string detail)
    {
        const string marker = " use ";
        int at = detail.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? detail[(at + marker.Length)..].Trim().TrimEnd('.') : detail.Trim();
    }

    /// <summary>"42s", "3m 12s", "1h 05m".</summary>
    public static string Duration(double seconds)
    {
        int s = Math.Max(0, (int)seconds);
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {s % 3600 / 60:00}m";
    }
}
