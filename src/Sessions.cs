using System.IO;

namespace ClaudeStatusBar;

/// <summary>
/// Reads every session's status from sessions.d (one JSON file per session, written by the
/// hooks), re-parsing a file only when it changes. Sessions are returned in the order they were
/// first seen, so their dots keep their places. Each entry's SessionId is its file name.
///
/// Empty files (written by hooks older than per-session status) are skipped; when nothing
/// parseable remains, callers fall back to state.json. Idle or finished sessions with no event
/// for <see cref="LeftoverSeconds"/> are hidden: those are left behind by crashed sessions.
/// </summary>
public sealed class SessionStore
{
    public const double LeftoverSeconds = 12 * 3600;

    private readonly string _dir;
    private readonly Func<double> _now;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _order;

    private sealed record Entry(DateTime LastWrite, ClaudeState State, long Order);

    /// <param name="dir">Status-bar data directory (sessions.d is inside); defaults to <see cref="StateStore.Dir"/>.</param>
    /// <param name="now">Clock in unix seconds; defaults to the system clock.</param>
    public SessionStore(string? dir = null, Func<double>? now = null)
    {
        _dir = Path.Combine(dir ?? StateStore.Dir, "sessions.d");
        _now = now ?? StateStore.UnixNow;
    }

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

        double now = _now();
        return _cache.Values
            .OrderBy(e => e.Order)
            .Select(e => StateStore.Effective(e.State, now))
            .Where(s => !(s.State is "idle" or "done" && now - s.Ts > LeftoverSeconds))
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
/// (awaiting permission, then working, then just finished, then idle). Focus is sticky: the
/// current session keeps it while it is still in the highest class present, so a session that
/// starts working later doesn't take over the display.
/// </summary>
public static class SessionFocus
{
    public static int Rank(string state) => state switch
    {
        "permission" => 3,
        "thinking" or "tool" => 2,
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
