using System.IO;
using System.Text.Json;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class UsageUpdaterTests : IDisposable
{
    private readonly TestDir _dir = new();
    private readonly List<string> _log = new();

    private string UsagePath => Path.Combine(_dir.Path, "usage.json");
    private UsageUpdater NewUpdater() => new(source: null, home: _dir.Path, output: new UsageFile(UsagePath, _log.Add));

    private void WriteClaudeJson(long fetchedAtMs, double fiveHour, double sevenDay) =>
        _dir.Write(Path.Combine(_dir.Path, ".claude.json"), new
        {
            numStartups = 12,
            oauthAccount = new { emailAddress = "someone@example.com" },
            cachedUsageUtilization = new
            {
                fetchedAtMs,
                utilization = new
                {
                    five_hour = new { utilization = fiveHour, resets_at = "2026-10-15T20:09:59.550255+00:00" },
                    seven_day = new { utilization = sevenDay, resets_at = "2026-10-16T15:59:59.550282+00:00" },
                },
            },
        });

    private JsonElement Usage() => JsonDocument.Parse(File.ReadAllText(UsagePath)).RootElement;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Copies_Claude_Codes_cached_usage_into_usage_json()
    {
        WriteClaudeJson(1_800_000_000_000, 31, 24);

        await NewUpdater().TickAsync(CancellationToken.None);

        var u = Usage();
        Assert.Equal("cache", u.GetProperty("source").GetString());
        Assert.Equal(1_800_000_000, u.GetProperty("ts").GetDouble());
        Assert.Equal(31, u.GetProperty("fiveHour").GetProperty("pct").GetDouble());
        Assert.Equal(24, u.GetProperty("sevenDay").GetProperty("pct").GetDouble());
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T20:10:00Z").ToUnixTimeSeconds(), u.GetProperty("fiveHour").GetProperty("resetsAt").GetDouble());
    }

    [Fact]
    public async Task Uses_only_the_cached_usage_key_of_claude_json()
    {
        WriteClaudeJson(1_800_000_000_000, 31, 24);
        await NewUpdater().TickAsync(CancellationToken.None);

        var written = File.ReadAllText(UsagePath);
        Assert.DoesNotContain("someone@example.com", written);
        Assert.DoesNotContain("numStartups", written);
        Assert.All(_log, line => Assert.DoesNotContain("someone@example.com", line));
    }

    [Fact]
    public async Task An_older_cache_never_replaces_newer_values()
    {
        _dir.Write(UsagePath, new { fiveHour = new { pct = 50, resetsAt = 0 }, sevenDay = (object?)null, source = "statusline", ts = 1_800_000_100 });
        WriteClaudeJson(1_800_000_000_000, 31, 24); // fetched 100 s before the statusLine values

        await NewUpdater().TickAsync(CancellationToken.None);

        Assert.Equal("statusline", Usage().GetProperty("source").GetString());
        Assert.Equal(50, Usage().GetProperty("fiveHour").GetProperty("pct").GetDouble());
    }

    [Fact]
    public async Task Missing_or_unreadable_claude_json_changes_nothing()
    {
        var updater = NewUpdater();
        await updater.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(UsagePath));

        _dir.Write(Path.Combine(_dir.Path, ".claude.json"), "{ not json");
        await updater.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(UsagePath));
    }

    [Fact]
    public async Task Without_an_online_source_it_says_it_uses_local_sources_only_once()
    {
        var updater = NewUpdater();
        await updater.TickAsync(CancellationToken.None);
        await updater.TickAsync(CancellationToken.None);
        Assert.Single(_log, line => line.StartsWith("cache") && line.Contains("cached usage"));
    }

    [Fact]
    public async Task An_online_source_runs_on_each_tick_and_writes_through_the_same_file()
    {
        var source = new FakeSource();
        var updater = new UsageUpdater(source, _dir.Path, new UsageFile(UsagePath, _log.Add));
        await updater.TickAsync(CancellationToken.None);
        await updater.TickAsync(CancellationToken.None);
        updater.Refresh();

        Assert.Equal(2, source.Ticks);
        Assert.Equal(1, source.Refreshes);
        Assert.Equal("api", Usage().GetProperty("source").GetString());
    }

    private sealed class FakeSource : IUsageSource
    {
        public int Ticks, Refreshes;

        public Task TickAsync(UsageFile output, CancellationToken ct)
        {
            Ticks++;
            using var doc = JsonDocument.Parse("""{"five_hour":{"utilization":10,"resets_at":0}}""");
            output.Write(UsageFile.Parse(doc.RootElement)!, "api", 1_800_000_000 + Ticks);
            return Task.CompletedTask;
        }

        public void Refresh() => Refreshes++;
    }
}

public class UsageFileTests : IDisposable
{
    private readonly TestDir _dir = new();
    private readonly List<string> _log = new();

    public void Dispose() => _dir.Dispose();

    private static Dictionary<string, object?> Usage(double five) =>
        UsageFile.Parse(JsonDocument.Parse($$$"""{"five_hour":{"utilization":{{{five}}},"resets_at":0}}""").RootElement)!;

    [Fact]
    public void Unchanged_values_are_not_rewritten_or_logged_again()
    {
        var file = new UsageFile(Path.Combine(_dir.Path, "usage.json"), _log.Add);
        file.Write(Usage(10), "api", 1);
        file.Write(Usage(10), "api", 2);
        file.Write(Usage(11), "api", 3);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public void Status_lines_are_logged_only_when_they_change()
    {
        var file = new UsageFile(Path.Combine(_dir.Path, "usage.json"), _log.Add);
        file.Status("api", "ok");
        file.Status("api", "ok");
        file.Status("api", "HTTP 500");
        file.Status("api", "ok");
        Assert.Equal(3, _log.Count);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"five_hour":{"utilization":"x"}}""")]
    public void Unrecognized_responses_parse_to_nothing(string json) =>
        Assert.Null(UsageFile.Parse(JsonDocument.Parse(json).RootElement));
}
