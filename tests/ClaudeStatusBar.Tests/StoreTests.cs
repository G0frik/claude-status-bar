using System.IO;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class UsageStoreTests : IDisposable
{
    private const double Now = 1_800_000_000;
    private readonly TestDir _dir = new();

    private UsageStore NewStore() => new(_dir.Path, () => Now);
    private void WriteUsage(object usage) => _dir.Write(System.IO.Path.Combine(_dir.Path, "usage.json"), usage);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Missing_file_means_no_data()
    {
        var u = NewStore().Poll();
        Assert.False(u.HasData);
    }

    [Fact]
    public void Parses_windows_source_and_timestamp()
    {
        WriteUsage(new { fiveHour = new { pct = 31, resetsAt = Now + 3600 }, sevenDay = new { pct = 24.5, resetsAt = Now + 86400 }, source = "api", ts = Now - 10 });

        var u = NewStore().Poll();
        Assert.Equal(31, u.FiveHour!.Percent);
        Assert.Equal(24.5, u.SevenDay!.Percent);
        Assert.Equal("api", u.Source);
        Assert.Equal(Now - 10, u.UpdatedAt);
    }

    [Fact]
    public void Window_past_its_reset_time_reads_as_empty()
    {
        WriteUsage(new { fiveHour = new { pct = 97, resetsAt = Now - 1 }, sevenDay = new { pct = 40, resetsAt = Now + 10 } });

        var u = NewStore().Poll();
        Assert.Equal(new UsageWindow(0, 0), u.FiveHour);
        Assert.Equal(40, u.SevenDay!.Percent);
    }

    [Fact]
    public void Absent_window_is_null_and_percent_is_clamped()
    {
        WriteUsage(new { fiveHour = new { pct = 140, resetsAt = 0 } });

        var u = NewStore().Poll();
        Assert.Equal(100, u.FiveHour!.Percent);
        Assert.Null(u.SevenDay);
    }

    [Fact]
    public void Deleting_the_file_clears_the_data()
    {
        var store = NewStore();
        WriteUsage(new { fiveHour = new { pct = 10, resetsAt = 0 } });
        Assert.True(store.Poll().HasData);

        File.Delete(System.IO.Path.Combine(_dir.Path, "usage.json"));
        Assert.False(store.Poll().HasData);
    }
}

public class StateStoreTests : IDisposable
{
    private const double Now = 1_800_000_000;
    private readonly TestDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Reads_state_json_for_hooks_without_per_session_status()
    {
        _dir.Write(System.IO.Path.Combine(_dir.Path, "state.json"), new { state = "tool", label = "Editing", sessionId = "s1", startedAt = Now - 3, ts = Now });

        var s = new StateStore(_dir.Path, () => Now).Poll();
        Assert.Equal("tool", s.State);
        Assert.Equal("Editing", s.Label);
        Assert.Equal("s1", s.SessionId);
    }

    [Fact]
    public void Missing_state_json_is_idle()
    {
        Assert.Equal("idle", new StateStore(_dir.Path, () => Now).Poll().State);
    }

    [Fact]
    public void Counts_one_session_per_file()
    {
        _dir.WriteSession("a", "");
        _dir.WriteSession("b", new { state = "idle" });
        Assert.Equal(2, new StateStore(_dir.Path).SessionCount());
    }
}
