using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>
/// Online usage source, compiled into the online build only and used only when the user opts in
/// (<see cref="Settings.UsageApiOptIn"/>): Anthropic's OAuth usage endpoint (the one behind Claude
/// Code's /usage), using the access token Claude Code keeps in ~/.claude/.credentials.json,
/// read-only. It never refreshes the token (that would rotate Claude Code's own login).
///
/// The endpoint rate-limits frequent callers, so it is polled sparingly: every
/// <see cref="Settings.UsageRefreshMinutes"/> while Claude Code is active and for one interval
/// after (to catch a turn's final numbers), not at all while idle, with exponential backoff after
/// a rate-limit or error response. The endpoint is undocumented, so any unexpected response leaves
/// the previous values in place.
///
/// Requests can only go to <see cref="Endpoint"/>: redirects are not followed and
/// <see cref="PinnedHostHandler"/> refuses any other URI. The token is read just before each
/// request, set on that request only, and never stored, logged or written anywhere.
/// </summary>
public sealed class UsageFetcher : IUsageSource, IDisposable
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    private const int MaxResponseBytes = 64 * 1024; // the real response is about 2 KB
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private static readonly string DefaultCredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly HttpClient _http;
    private readonly string _credentialsPath;
    private readonly StateStore _state;
    private readonly Settings _settings;

    private DateTime _nextFetch = DateTime.MinValue; // UTC; MinValue fetches on the next tick
    private bool _fetchedOnce;
    private int _failures;                            // consecutive failed fetches, for backoff

    public UsageFetcher() : this(CreateHandler(), DefaultCredentialsPath, new StateStore(), Settings.Shared) { }

    /// <param name="handler">Transport; tests pass a fake. Wrapped in <see cref="PinnedHostHandler"/>.</param>
    internal UsageFetcher(HttpMessageHandler handler, string credentialsPath, StateStore state, Settings settings)
    {
        _credentialsPath = credentialsPath;
        _state = state;
        _settings = settings;
        _http = new HttpClient(new PinnedHostHandler(handler))
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClaudeStatusBar/{AppVersion}");
    }

    /// <summary>No redirects (a redirect could only lead somewhere other than the endpoint) and no cookies; the system proxy is kept.</summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    };

    public void Refresh() => _nextFetch = DateTime.MinValue;

    public void Dispose() => _http.Dispose();

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(_settings.UsageRefreshMinutes, 1, 60));

    public async Task TickAsync(UsageFile output, CancellationToken ct)
    {
        if (!_settings.UsageApiOptIn)
        {
            output.Status("cache", "usage API off; using Claude Code's cached usage in ~/.claude.json and the terminal statusLine");
            return;
        }
        if (DateTime.UtcNow < _nextFetch) return;
        if (_fetchedOnce && !ClaudeActive())
        {
            output.Status("api", "idle: paused until Claude Code is active");
            return;
        }

        try { await FetchAsync(output, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Backoff(output, $"error: {ex.Message}", null); }
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

    private async Task FetchAsync(UsageFile output, CancellationToken ct)
    {
        _fetchedOnce = true;
        var token = ReadAccessToken();
        if (token is null)
        {
            _nextFetch = DateTime.UtcNow + Interval;
            output.Status("api", "skipped: no valid claude.ai sign-in token (not signed in, or expired until Claude Code refreshes it)");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new("Bearer", token);
        token = null; // locals of an async method live in its state machine, past the await
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            var reason = response.StatusCode == HttpStatusCode.TooManyRequests ? "rate limited (429)"
                : code is >= 300 and < 400 ? $"redirect refused ({code})"
                : $"HTTP {code}";
            Backoff(output, reason, response.Headers.RetryAfter?.Delta);
            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (UsageFile.Parse(doc.RootElement) is not { } usage)
        {
            Backoff(output, "unrecognized response", null);
            return;
        }
        _failures = 0;
        _nextFetch = DateTime.UtcNow + Interval;
        output.Status("api", $"ok, refreshing every {Interval.TotalMinutes:0} min while Claude Code is active");
        output.Write(usage, "api", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    // Wait interval × 2^failures (at least Retry-After, at most MaxBackoff) before the next try.
    private void Backoff(UsageFile output, string reason, TimeSpan? retryAfter)
    {
        _failures++;
        var wait = TimeSpan.FromTicks(Interval.Ticks * (1L << Math.Min(_failures, 5)));
        if (retryAfter > wait) wait = retryAfter.Value;
        if (wait > MaxBackoff) wait = MaxBackoff;
        _nextFetch = DateTime.UtcNow + wait;
        output.Status("api", $"{reason}, next try in {wait.TotalMinutes:0} min");
    }

    // Only claudeAiOauth.accessToken and expiresAt are read; the document is released right away.
    private string? ReadAccessToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(SharedFile.ReadAllText(_credentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("accessToken", out var t) || t.ValueKind != JsonValueKind.String) return null;
            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number &&
                exp.GetDouble() <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return null;
            return t.GetString();
        }
        catch { return null; }
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.5.0";
}

/// <summary>
/// Refuses any request whose URI isn't exactly <see cref="UsageFetcher.Endpoint"/>, so a later code
/// change can't send the sign-in token anywhere else.
/// </summary>
internal sealed class PinnedHostHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        bool allowed = uri is { IsAbsoluteUri: true }
            && uri.Port == 443
            && Uri.Compare(uri, UsageFetcher.Endpoint, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;
        if (!allowed) throw new InvalidOperationException($"Refused a request to {uri}: only {UsageFetcher.Endpoint} is allowed.");
        return base.SendAsync(request, ct);
    }
}
