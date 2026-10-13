using System.IO;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>
/// Status written by the Claude Code hooks. Field names match the JSON keys the hooks emit.
/// </summary>
public sealed class ClaudeState
{
    public string State { get; set; } = "idle"; // idle | thinking | tool | permission | done
    public string Label { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Project { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Transcript { get; set; } = "";
    public double StartedAt { get; set; }
    public double Ts { get; set; }

    /// <summary>The session's Claude Code process (0 when unknown, e.g. older hooks).</summary>
    public int Pid { get; set; }

    /// <summary>What a permission request is for: the tool name or the notification message.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Length of the turn that just finished, set with the "done" state.</summary>
    public double LastTurnSeconds { get; set; }
}

/// <summary>
/// Reads the hook-written status file, re-parsing only when it changes on disk, and counts the
/// active sessions tracked under sessions.d. Active states older than 15 minutes, or whose
/// transcript ends with an interruption marker, are reported as idle.
/// </summary>
public sealed class StateStore
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "statusbar");

    /// <summary>Active states with no hook event for this long are reported as idle.</summary>
    public const double StaleActiveSeconds = 900;

    private readonly string _statePath;
    private readonly string _sessionsDir;
    private readonly Func<double> _now;

    private DateTime _lastWrite = DateTime.MinValue;
    private ClaudeState _current = new();

    /// <param name="dir">Status-bar data directory; defaults to <see cref="Dir"/>.</param>
    /// <param name="now">Clock in unix seconds; defaults to the system clock.</param>
    public StateStore(string? dir = null, Func<double>? now = null)
    {
        dir ??= Dir;
        _statePath = Path.Combine(dir, "state.json");
        _sessionsDir = Path.Combine(dir, "sessions.d");
        _now = now ?? UnixNow;
    }

    public static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Re-read the status file if it changed, then return the effective state.</summary>
    public ClaudeState Poll()
    {
        try
        {
            var info = new FileInfo(_statePath);
            if (info.Exists && info.LastWriteTimeUtc != _lastWrite)
            {
                _current = Parse(SharedFile.ReadAllText(_statePath));
                _lastWrite = info.LastWriteTimeUtc; // only after a successful read, so a failed one retries
            }
        }
        catch { }

        return Effective(_current, _now());
    }

    /// <summary>Number of active sessions (one file per session under sessions.d).</summary>
    public int SessionCount()
    {
        try { return Directory.Exists(_sessionsDir) ? Directory.GetFiles(_sessionsDir).Length : 0; }
        catch { return 0; }
    }

    /// <summary>Parse a hook-written status (state.json or a sessions.d entry). Throws on invalid JSON.</summary>
    internal static ClaudeState Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string S(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        double N(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var state = S("state");
        return new ClaudeState
        {
            State = state.Length == 0 ? "idle" : state,
            Label = S("label"),
            Tool = S("tool"),
            Project = S("project"),
            SessionId = S("sessionId"),
            Transcript = S("transcript"),
            StartedAt = N("startedAt"),
            Ts = N("ts"),
            Pid = (int)N("pid"),
            Detail = S("detail"),
            LastTurnSeconds = N("lastTurnSeconds"),
        };
    }

    /// <summary>
    /// The state to display: an active state with no event for <see cref="StaleActiveSeconds"/>,
    /// or whose transcript ends with an interruption marker, is idle. Ts is kept, so the time of
    /// the last real event stays known.
    /// </summary>
    internal static ClaudeState Effective(ClaudeState s, double now)
    {
        if (s.State is "thinking" or "tool" or "permission")
        {
            if (now - s.Ts > StaleActiveSeconds ||
                (!string.IsNullOrEmpty(s.Transcript) && LastLineContains(s.Transcript, "interrupted by user")))
            {
                return new ClaudeState { State = "idle", Project = s.Project, SessionId = s.SessionId, Ts = s.Ts, Pid = s.Pid };
            }
        }
        return s;
    }

    private static bool LastLineContains(string path, string needle)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - 8192);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            for (int i = text.Length; i > 0;)
            {
                int nl = text.LastIndexOf('\n', i - 1);
                var line = text.Substring(nl + 1, i - (nl + 1)).Trim();
                if (line.Length > 0) return line.Contains(needle, StringComparison.OrdinalIgnoreCase);
                i = nl;
                if (nl < 0) break;
            }
        }
        catch { }
        return false;
    }
}
