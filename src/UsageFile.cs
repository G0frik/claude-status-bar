using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>
/// usage.json, which <see cref="UsageStore"/> reads, and the usage log. Every usage source writes
/// through here: the same rules as statusline.js (skip unchanged values, replace atomically), and
/// outcomes logged only when they change, so a steady poll doesn't fill the log.
/// </summary>
public sealed class UsageFile
{
    private readonly string _path;
    private readonly Action<string> _log;
    private string _lastStatus = "";

    /// <param name="path">usage.json; defaults to the status-bar data directory.</param>
    /// <param name="log">Where outcome lines go; defaults to <see cref="UsageLog"/>.</param>
    public UsageFile(string? path = null, Action<string>? log = null)
    {
        _path = path ?? Path.Combine(StateStore.Dir, "usage.json");
        _log = log ?? UsageLog.Write;
    }

    /// <summary>Map a usage response to the usage.json shape: { fiveHour, sevenDay } with { pct, resetsAt }.</summary>
    public static Dictionary<string, object?>? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var fiveHour = Window(root, "five_hour");
        var sevenDay = Window(root, "seven_day");
        if (fiveHour is null && sevenDay is null) return null;
        return new() { ["fiveHour"] = fiveHour, ["sevenDay"] = sevenDay };
    }

    private static Dictionary<string, double>? Window(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return null;

        double resetsAt = 0;
        if (w.TryGetProperty("resets_at", out var r))
        {
            if (r.ValueKind == JsonValueKind.Number) resetsAt = r.GetDouble();
            else if (r.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                resetsAt = Math.Round(dt.ToUnixTimeMilliseconds() / 1000.0); // "…:59.55" -> ":00", as the statusLine reports it
        }
        return new() { ["pct"] = Math.Clamp(u.GetDouble(), 0, 100), ["resetsAt"] = resetsAt };
    }

    /// <summary>When the current values were measured (unix seconds), 0 if unknown.</summary>
    public double ReadTimestamp()
    {
        try
        {
            using var doc = JsonDocument.Parse(SharedFile.ReadAllText(_path));
            return doc.RootElement.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0;
        }
        catch { return 0; }
    }

    // ts is when the values were measured: now for the API, Claude Code's fetch time for the cache.
    public void Write(Dictionary<string, object?> usage, string source, long ts)
    {
        var limits = JsonSerializer.Serialize(new[] { usage["fiveHour"], usage["sevenDay"] });
        try
        {
            using var prev = JsonDocument.Parse(SharedFile.ReadAllText(_path));
            var old = JsonSerializer.Serialize(new object?[]
            {
                prev.RootElement.TryGetProperty("fiveHour", out var a) ? a : null,
                prev.RootElement.TryGetProperty("sevenDay", out var b) ? b : null,
            });
            if (old == limits) return;
        }
        catch { }

        usage["source"] = source;
        usage["ts"] = ts;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = $"{_path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(usage));
        SharedFile.Replace(tmp, _path);
        _log($"{source,-10} wrote usage.json  5h={Pct(usage["fiveHour"])}  7d={Pct(usage["sevenDay"])}");
    }

    /// <summary>Log a source's outcome, only when it differs from the last one logged.</summary>
    public void Status(string source, string status)
    {
        var line = $"{source,-10} {status}";
        if (line == _lastStatus) return;
        _lastStatus = line;
        _log(line);
    }

    private static string Pct(object? window) =>
        window is Dictionary<string, double> w ? $"{w["pct"]:0.#}%" : "-";
}
