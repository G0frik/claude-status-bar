using System.IO;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>One plan usage-limit window: percent used (0–100) and when it resets (unix seconds, 0 if unknown).</summary>
public sealed record UsageWindow(double Percent, double ResetsAt);

/// <summary>
/// claude.ai plan usage limits. A null window has no data. Source names the writer of usage.json
/// ("api", "cache" or "statusline"); UpdatedAt is when the values were measured (unix seconds).
/// </summary>
public sealed record UsageLimits(UsageWindow? FiveHour, UsageWindow? SevenDay, string Source = "", double UpdatedAt = 0)
{
    public bool HasData => FiveHour is not null || SevenDay is not null;
}

/// <summary>
/// Reads the usage file written by the statusLine script or <see cref="UsageUpdater"/>, re-parsing only when it changes on
/// disk. A window whose reset time has passed is reported as empty, since Claude Code stops
/// sending a window once it resets.
/// </summary>
public sealed class UsageStore
{
    private readonly string _usagePath;
    private readonly Func<double> _now;

    /// <param name="dir">Status-bar data directory; defaults to <see cref="StateStore.Dir"/>.</param>
    /// <param name="now">Clock in unix seconds; defaults to the system clock.</param>
    public UsageStore(string? dir = null, Func<double>? now = null)
    {
        _usagePath = Path.Combine(dir ?? StateStore.Dir, "usage.json");
        _now = now ?? StateStore.UnixNow;
    }

    private DateTime _lastWrite = DateTime.MinValue;
    private UsageLimits _current = new(null, null);

    /// <summary>Re-read the usage file if it changed, then return the effective limits.</summary>
    public UsageLimits Poll()
    {
        try
        {
            var info = new FileInfo(_usagePath);
            if (!info.Exists)
            {
                _lastWrite = DateTime.MinValue;
                _current = new(null, null);
            }
            else if (info.LastWriteTimeUtc != _lastWrite)
            {
                _current = Parse(SharedFile.ReadAllText(_usagePath));
                _lastWrite = info.LastWriteTimeUtc; // only after a successful read, so a failed one retries
            }
        }
        catch { }

        return _current with { FiveHour = Effective(_current.FiveHour), SevenDay = Effective(_current.SevenDay) };
    }

    private static UsageLimits Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        UsageWindow? W(string key)
        {
            if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
            if (!w.TryGetProperty("pct", out var pct) || pct.ValueKind != JsonValueKind.Number) return null;
            double resetsAt = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : 0;
            return new UsageWindow(Math.Clamp(pct.GetDouble(), 0, 100), resetsAt);
        }
        string source = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() ?? "" : "";
        double ts = root.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
        return new UsageLimits(W("fiveHour"), W("sevenDay"), source, ts);
    }

    private UsageWindow? Effective(UsageWindow? w)
    {
        if (w is null || w.ResetsAt <= 0) return w;
        return w.ResetsAt <= _now() ? new UsageWindow(0, 0) : w;
    }
}
