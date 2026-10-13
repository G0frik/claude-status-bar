using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>
/// Keeps usage.json (read by <see cref="UsageStore"/>) current for clients that never run the
/// statusLine command, such as the VS Code extension; statusline.js writes the same file from the
/// CLI. Two modes, chosen by <see cref="Settings.FetchUsageOnline"/>:
///
/// Online: fetches the plan usage limits from Anthropic's OAuth usage endpoint (the one behind
/// Claude Code's /usage), using the access token Claude Code keeps in ~/.claude/.credentials.json,
/// read-only. It never refreshes the token (that would rotate Claude Code's own login); an expired
/// or rejected token is simply retried later, after Claude Code has refreshed it. The endpoint is
/// undocumented, so any unexpected response leaves the previous values in place.
///
/// Offline: no network. Reads the copy of that same response Claude Code caches in ~/.claude.json
/// (refreshed whenever Claude Code itself fetches usage), and only when it is newer than what
/// usage.json already holds.
///
/// Outcomes go to <see cref="UsageLog"/>.
/// </summary>
public sealed class UsageFetcher : IDisposable
{
    public static UsageFetcher? Instance { get; private set; }

    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string CredentialsPath = Path.Combine(Home, ".claude", ".credentials.json");
    private static readonly string ClaudeJsonPath = Path.Combine(Home, ".claude.json");
    private static readonly string UsagePath = Path.Combine(StateStore.Dir, "usage.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string _lastStatus = "";

    public UsageFetcher()
    {
        Instance = this;
        _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClaudeStatusBar/{AppVersion}");
    }

    public void Start() => _ = RunAsync(_cts.Token);

    /// <summary>Run the next update now instead of waiting out the interval (e.g. after a mode change).</summary>
    public void Refresh()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try { delay = Settings.Shared.FetchUsageOnline ? await FetchOnlineAsync(ct) : ReadCache(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Status("api", $"error: {ex.Message}"); delay = ErrorBackoff; }

            try { await _wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ----- online: usage endpoint -----

    /// <summary>One fetch; returns how long to wait before the next.</summary>
    private async Task<TimeSpan> FetchOnlineAsync(CancellationToken ct)
    {
        var token = ReadAccessToken();
        if (token is null)
        {
            Status("api", "skipped: no valid claude.ai sign-in token (not signed in, or expired until Claude Code refreshes it)");
            return Interval;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? ErrorBackoff;
            if (retry < Interval) retry = Interval;
            Status("api", $"rate limited (429), retrying in {retry.TotalSeconds:0}s");
            return retry;
        }
        if (!response.IsSuccessStatusCode)
        {
            Status("api", $"HTTP {(int)response.StatusCode}, retrying in {ErrorBackoff.TotalMinutes:0} min");
            return ErrorBackoff;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (ToUsage(doc.RootElement) is not { } usage)
        {
            Status("api", "unrecognized response");
            return Interval;
        }
        Status("api", $"ok, polling every {Interval.TotalSeconds:0}s");
        Write(usage, "api", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return Interval;
    }

    private static string? ReadAccessToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("accessToken", out var t) || t.ValueKind != JsonValueKind.String) return null;
            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number &&
                exp.GetDouble() <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return null;
            return t.GetString();
        }
        catch { return null; }
    }

    // ----- offline: Claude Code's cache -----

    private TimeSpan ReadCache()
    {
        Status("cache", "online fetching off; using Claude Code's cached usage in ~/.claude.json and the terminal statusLine");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ClaudeJsonPath));
            if (!doc.RootElement.TryGetProperty("cachedUsageUtilization", out var cache) ||
                !cache.TryGetProperty("fetchedAtMs", out var at) || at.ValueKind != JsonValueKind.Number ||
                !cache.TryGetProperty("utilization", out var utilization) ||
                ToUsage(utilization) is not { } usage)
                return Interval;

            long fetchedAt = (long)(at.GetDouble() / 1000);
            if (fetchedAt > ReadUsageTimestamp()) Write(usage, "cache", fetchedAt);
        }
        catch { } // missing, or mid-write by Claude Code; try again next round
        return Interval;
    }

    private static double ReadUsageTimestamp()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(UsagePath));
            return doc.RootElement.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0;
        }
        catch { return 0; }
    }

    // ----- usage.json -----

    /// <summary>Map a usage response to the usage.json shape: { fiveHour, sevenDay } with { pct, resetsAt }.</summary>
    private static Dictionary<string, object?>? ToUsage(JsonElement root)
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

    // Same file and write rules as statusline.js: skip unchanged values, replace atomically.
    // ts is when the values were measured: now for the API, Claude Code's fetch time for the cache.
    private static void Write(Dictionary<string, object?> usage, string source, long ts)
    {
        var limits = JsonSerializer.Serialize(new[] { usage["fiveHour"], usage["sevenDay"] });
        try
        {
            using var prev = JsonDocument.Parse(File.ReadAllText(UsagePath));
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
        Directory.CreateDirectory(StateStore.Dir);
        var tmp = $"{UsagePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(usage));
        File.Move(tmp, UsagePath, overwrite: true);
        UsageLog.Write($"{source,-10} wrote usage.json  5h={Pct(usage["fiveHour"])}  7d={Pct(usage["sevenDay"])}");
    }

    private static string Pct(object? window) =>
        window is Dictionary<string, double> w ? $"{w["pct"]:0.#}%" : "-";

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0";

    // Log outcomes only when they change, so a steady poll doesn't fill the log.
    private void Status(string source, string status)
    {
        var line = $"{source,-10} {status}";
        if (line == _lastStatus) return;
        _lastStatus = line;
        UsageLog.Write(line);
    }
}
