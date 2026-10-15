using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>The online build's usage API fetch. Compiled only when Offline=false; no real network.</summary>
public class UsageFetcherTests : IDisposable
{
    private const string Token = "sk-ant-oat01-SENTINEL-TOKEN-must-never-leak";
    private const string UsageJson = """{"five_hour":{"utilization":31,"resets_at":"2026-10-15T20:00:00Z"},"seven_day":{"utilization":24,"resets_at":null}}""";

    private readonly TestDir _dir = new();
    private readonly List<string> _log = new();
    private readonly FakeServer _server = new();

    private string CredentialsPath => Path.Combine(_dir.Path, ".credentials.json");
    private string UsagePath => Path.Combine(_dir.Path, "usage.json");

    public UsageFetcherTests()
    {
        _dir.Write(CredentialsPath, new
        {
            claudeAiOauth = new { accessToken = Token, refreshToken = "refresh-must-never-leak", expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() },
        });
    }

    public void Dispose() => _dir.Dispose();

    private async Task TickOnce(bool optedIn = true)
    {
        var settings = new Settings { UsageApiOptIn = optedIn };
        using var fetcher = new UsageFetcher(_server, CredentialsPath, new StateStore(_dir.Path), settings);
        await fetcher.TickAsync(new UsageFile(UsagePath, _log.Add), CancellationToken.None);
    }

    private void AssertNoSecretAnywhere()
    {
        Assert.All(_log, line => Assert.DoesNotContain("SENTINEL", line));
        Assert.All(_log, line => Assert.DoesNotContain("refresh-must-never-leak", line));
        foreach (var file in Directory.GetFiles(_dir.Path).Where(f => f != CredentialsPath))
            Assert.DoesNotContain("SENTINEL", File.ReadAllText(file));
    }

    [Fact]
    public void The_usage_API_is_off_by_default_and_the_retired_key_does_not_turn_it_on()
    {
        Assert.False(new Settings().UsageApiOptIn);
        Assert.False(Settings.Parse("""{ "FetchUsageOnline": true, "ShowTimer": false }""").UsageApiOptIn);
        Assert.True(Settings.Parse("""{ "UsageApiOptIn": true }""").UsageApiOptIn);
    }

    [Fact]
    public async Task Opted_out_it_never_reads_the_credentials_or_sends_anything()
    {
        await TickOnce(optedIn: false);
        Assert.Empty(_server.Requests);
        Assert.DoesNotContain(_log, line => line.Contains("token"));   // it never got as far as reading one
        Assert.False(File.Exists(UsagePath));
    }

    [Fact]
    public async Task Opted_in_it_sends_the_token_once_to_the_endpoint_and_writes_usage()
    {
        _server.Respond = _ => Json(HttpStatusCode.OK, UsageJson);
        await TickOnce();

        var request = Assert.Single(_server.Requests);
        Assert.Equal(UsageFetcher.Endpoint, request.Uri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Contains("\"source\":\"api\"", File.ReadAllText(UsagePath));
        AssertNoSecretAnywhere();
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_or_redirected_response_is_one_request_backs_off_and_leaks_nothing(HttpStatusCode status)
    {
        _server.Respond = _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://attacker.example/collect");
            return response;
        };
        await TickOnce();

        Assert.Single(_server.Requests);
        Assert.Contains(_log, line => line.Contains("next try in"));
        if ((int)status is >= 300 and < 400) Assert.Contains(_log, line => line.Contains("redirect refused"));
        Assert.False(File.Exists(UsagePath));
        AssertNoSecretAnywhere();
    }

    [Fact]
    public async Task A_transport_error_is_logged_without_the_token()
    {
        _server.Respond = _ => throw new HttpRequestException("No such host is known. (api.anthropic.com:443)");
        await TickOnce();
        Assert.Contains(_log, line => line.Contains("No such host"));
        AssertNoSecretAnywhere();
    }

    [Fact]
    public async Task An_oversized_response_is_refused()
    {
        _server.Respond = _ => Json(HttpStatusCode.OK, "{\"pad\":\"" + new string('x', 200_000) + "\"}");
        await TickOnce();
        Assert.False(File.Exists(UsagePath));
        Assert.Contains(_log, line => line.Contains("error:"));
    }

    [Fact]
    public void The_real_handler_follows_no_redirects_and_keeps_no_cookies()
    {
        using var handler = UsageFetcher.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Theory]
    [InlineData("http://api.anthropic.com/api/oauth/usage")]
    [InlineData("https://api.anthropic.com:8443/api/oauth/usage")]
    [InlineData("https://api.anthropic.com.attacker.example/api/oauth/usage")]
    [InlineData("https://attacker.example/api/oauth/usage")]
    [InlineData("https://api.anthropic.com/api/oauth/usage?x=1")]
    [InlineData("https://api.anthropic.com/api/oauth/other")]
    [InlineData("https://user@api.anthropic.com/api/oauth/usage")]
    public async Task The_pinned_handler_refuses_every_other_destination(string uri)
    {
        using var client = new HttpClient(new PinnedHostHandler(_server));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(uri));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task The_pinned_handler_lets_the_endpoint_through()
    {
        _server.Respond = _ => Json(HttpStatusCode.OK, UsageJson);
        using var client = new HttpClient(new PinnedHostHandler(_server));
        using var response = await client.GetAsync(UsageFetcher.Endpoint);
        Assert.Equal(UsageFetcher.Endpoint, Assert.Single(_server.Requests).Uri);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Records requests (URI and Authorization only) and answers with <see cref="Respond"/>.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly List<(Uri? Uri, string? Authorization)> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(Respond(request));
        }
    }
}
