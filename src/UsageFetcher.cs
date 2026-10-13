using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>
/// Keeps usage.json (read by <see cref="UsageStore"/>) current for clients that never run the
/// statusLine command, such as the VS Code extension; statusline.js writes the same file from the
/// CLI. Two sources:
///
/// Cache (always, no network): the copy of the usage response Claude Code keeps in ~/.claude.json,
/// used whenever it is newer than what usage.json holds. It only updates when Claude Code itself
/// fetches usage, so on its own it can be hours old.
///
/// Online (when <see cref="Settings.FetchUsageOnline"/> is on): Anthropic's OAuth usage endpoint
/// (the one behind Claude Code's /usage), using the access token Claude Code keeps in
/// ~/.claude/.credentials.json, read-only. It never refreshes the token (that would rotate Claude
/// Code's own login). The endpoint rate-limits frequent callers, so it is polled sparingly: every
/// <see cref="Settings.UsageRefreshMinutes"/> while Claude Code is active and for one interval
/// after (to catch a turn's final numbers), not at all while idle, with exponential backoff after
/// a rate-limit or error response. The endpoint is undocumented, so any unexpected response leaves
/// the previous values in place.
///
/// Outcomes go to <see cref="UsageLog"/>.
/// </summary>
public sealed class UsageFetcher : IDisposable
{
    public static UsageFetcher? Instance { get; private set; }

    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);     // how often to re-check; cheap, no network
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string CredentialsPath = Path.Combine(Home, ".claude", ".credentials.json");
    private static readonly string ClaudeJsonPath = Path.Combine(Home, ".claude.json");
    private static readonly string UsagePath = Path.Combine(StateStore.Dir, "usage.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly StateStore _state = new();

    private DateTime _nextFetch = DateTime.MinValue; // UTC; MinValue fetches on the next tick
    private bool _fetchedOnce;
    private int _failures;                            // consecutive failed fetches, for backoff
    private DateTime _cacheWrite = DateTime.MinValue;
    private string _lastStatus = "";

    public UsageFetcher()
    {
        Instance = this;
        _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClaudeStatusBar/{AppVersion}");
    }

    public void Start() => _ = RunAsync(_cts.Token);

    /// <summary>Re-evaluate now and allow an immediate fetch (after a settings change).</summary>
    public void Refresh()
    {
        _nextFetch = DateTime.MinValue;
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    private static TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(Settings.Shared.UsageRefreshMinutes, 1, 60));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Backoff($"error: {ex.Message}", null); }

            try { await _wake.WaitAsync(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        ReadCacheIfChanged();

        if (!Settings.Shared.FetchUsageOnline)
        {
            Status("cache", "online fetching off; using Claude Code's cached usage in ~/.claude.json and the terminal statusLine");
            return;
        }
        if (DateTime.UtcNow < _nextFetch) return;
        if (_fetchedOnce && !ClaudeActive())
        {
            Status("api", "idle: paused until Claude Code is active");
            return;
        }
        await FetchOnlineAsync(ct);
    }

    // Active while a turn is running, and for one interval (plus a minute) after the last hook
    // event, so the fetch that follows a turn still happens.
    private bool ClaudeActive()
    {
        var s = _state.Poll();
        if (s.State is "thinking" or "tool" or "permission") return true;
        double sinceLastEvent = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.Ts;
        return sinceLastEvent < (Interval + TimeSpan.FromMinutes(1)).TotalSeconds;
    }

    // ----- online: usage endpoint -----

    private async Task FetchOnlineAsync(CancellationToken ct)
    {
        _fetchedOnce = true;
        var token = ReadAccessToken();
        if (token is null)
        {
            _nextFetch = DateTime.UtcNow + Interval;
            Status("api", "skipped: no valid claude.ai sign-in token (not signed in, or expired until Claude Code refreshes it)");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode == HttpStatusCode.TooManyRequests ? "rate limited (429)" : $"HTTP {(int)response.StatusCode}";
            Backoff(reason, response.Headers.RetryAfter?.Delta);
            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (ToUsage(doc.RootElement) is not { } usage)
        {
            Backoff("unrecognized response", null);
            return;
        }
        _failures = 0;
        _nextFetch = DateTime.UtcNow + Interval;
        Status("api", $"ok, refreshing every {Interval.TotalMinutes:0} min while Claude Code is active");
        Write(usage, "api", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    // Wait interval × 2^failures (at least Retry-After, at most MaxBackoff) before the next try.
    private void Backoff(string reason, TimeSpan? retryAfter)
    {
        _failures++;
        var wait = TimeSpan.FromTicks(Interval.Ticks * (1L << Math.Min(_failures, 5)));
        if (retryAfter > wait) wait = retryAfter.Value;
        if (wait > MaxBackoff) wait = MaxBackoff;
        _nextFetch = DateTime.UtcNow + wait;
        Status("api", $"{reason}, next try in {wait.TotalMinutes:0} min");
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

    // ----- cache: Claude Code's copy in ~/.claude.json -----

    private void ReadCacheIfChanged()
    {
        try
        {
            var info = new FileInfo(ClaudeJsonPath);
            if (!info.Exists || info.LastWriteTimeUtc == _cacheWrite) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(ClaudeJsonPath));
            _cacheWrite = info.LastWriteTimeUtc; // only after a successful read, so a failed one retries
            if (!doc.RootElement.TryGetProperty("cachedUsageUtilization", out var cache) ||
                !cache.TryGetProperty("fetchedAtMs", out var at) || at.ValueKind != JsonValueKind.Number ||
                !cache.TryGetProperty("utilization", out var utilization) ||
                ToUsage(utilization) is not { } usage)
                return;

            long fetchedAt = (long)(at.GetDouble() / 1000);
            if (fetchedAt > ReadUsageTimestamp()) Write(usage, "cache", fetchedAt);
        }
        catch { } // mid-write by Claude Code; try again next tick
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
