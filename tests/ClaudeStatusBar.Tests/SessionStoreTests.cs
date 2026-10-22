using System.IO;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class SessionStoreTests : IDisposable
{
    private const double Now = 1_800_000_000;
    private readonly TestDir _dir = new();
    private double _now = Now;

    private SessionStore NewStore() => new(_dir.Path, () => _now);

    private static object Status(string state, double ts = Now, string project = "proj", double startedAt = 0, string transcript = "") =>
        new { state, label = "", project, sessionId = "raw-id", transcript, startedAt, ts };

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Parses_json_sessions_and_uses_the_file_name_as_id()
    {
        _dir.WriteSession("abc", Status("thinking", startedAt: Now - 5));

        var s = Assert.Single(NewStore().Poll());
        Assert.Equal("abc", s.SessionId);
        Assert.Equal("thinking", s.State);
        Assert.Equal("proj", s.Project);
        Assert.Equal(Now - 5, s.StartedAt);
    }

    [Fact]
    public void Missing_directory_yields_no_sessions()
    {
        Directory.Delete(_dir.Sessions);
        Assert.Empty(NewStore().Poll());
    }

    [Fact]
    public void Skips_empty_legacy_files_garbage_and_temp_files_without_throwing()
    {
        _dir.WriteSession("legacy", "");
        _dir.WriteSession("garbage", "{not json");
        _dir.WriteSession("partial", "{\"state\":\"thin");
        _dir.WriteSession("x.123.tmp", Status("tool"));
        _dir.WriteSession("good", Status("tool"));

        var s = Assert.Single(NewStore().Poll());
        Assert.Equal("good", s.SessionId);
    }

    [Fact]
    public void Picks_up_a_file_that_becomes_valid_later()
    {
        var store = NewStore();
        _dir.WriteSession("a", "{\"state\":");
        Assert.Empty(store.Poll());

        var file = _dir.WriteSession("a", Status("tool"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal("tool", Assert.Single(store.Poll()).State);
    }

    [Fact]
    public void Re_reads_a_file_only_when_its_write_time_changes()
    {
        var store = NewStore();
        var file = _dir.WriteSession("a", Status("thinking"));
        var written = File.GetLastWriteTimeUtc(file);
        Assert.Equal("thinking", Assert.Single(store.Poll()).State);

        _dir.WriteSession("a", Status("permission"));
        File.SetLastWriteTimeUtc(file, written); // same timestamp: served from cache
        Assert.Equal("thinking", Assert.Single(store.Poll()).State);

        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));
        Assert.Equal("permission", Assert.Single(store.Poll()).State);
    }

    [Fact]
    public void Drops_sessions_whose_file_was_deleted()
    {
        var store = NewStore();
        var a = _dir.WriteSession("a", Status("idle"));
        _dir.WriteSession("b", Status("idle"));
        Assert.Equal(2, store.Poll().Count);

        File.Delete(a);
        Assert.Equal("b", Assert.Single(store.Poll()).SessionId);
    }

    [Fact]
    public void Keeps_first_seen_order_rather_than_directory_order()
    {
        var store = NewStore();
        _dir.WriteSession("zzz", Status("idle"));
        store.Poll();
        _dir.WriteSession("aaa", Status("idle"));

        Assert.Equal(["zzz", "aaa"], store.Poll().Select(s => s.SessionId));
    }

    [Fact]
    public void Active_session_with_no_event_for_15_minutes_is_idle_but_keeps_its_timestamp()
    {
        _dir.WriteSession("a", Status("tool", ts: Now - StateStore.StaleActiveSeconds - 1));

        var s = Assert.Single(NewStore().Poll());
        Assert.Equal("idle", s.State);
        Assert.Equal(Now - StateStore.StaleActiveSeconds - 1, s.Ts);
    }

    [Fact]
    public void Background_tasks_can_run_past_the_stale_timeout()
    {
        // A build or a Monitor left running sends no hook events until it ends.
        _dir.WriteSession("a", Status("background", ts: Now - StateStore.StaleActiveSeconds - 1));

        Assert.Equal("background", Assert.Single(NewStore().Poll()).State);
    }

    [Fact]
    public void Active_session_whose_transcript_ends_with_an_interruption_is_idle()
    {
        var transcript = System.IO.Path.Combine(_dir.Path, "t.jsonl");
        File.WriteAllText(transcript, "{\"a\":1}\n{\"text\":\"[Request interrupted by user]\"}\n");
        _dir.WriteSession("a", Status("thinking", transcript: transcript));

        Assert.Equal("idle", Assert.Single(NewStore().Poll()).State);
    }

    [Fact]
    public void Hides_idle_done_and_background_leftovers_after_12_hours_but_not_recent_ones()
    {
        double old = Now - SessionStore.LeftoverSeconds - 1;
        _dir.WriteSession("old-idle", Status("idle", ts: old));
        _dir.WriteSession("old-done", Status("done", ts: old));
        _dir.WriteSession("old-background", Status("background", ts: old));
        _dir.WriteSession("recent-idle", Status("idle", ts: Now - 60));

        Assert.Equal("recent-idle", Assert.Single(NewStore().Poll()).SessionId);
    }

    [Fact]
    public async Task Atomic_replaces_with_retry_never_lose_an_update_while_the_store_is_polling()
    {
        // Writers replace a session file by renaming a temp file over it. On Windows that rename
        // fails while the reader has the file open, so it must be retried (as hooks/atomic.js does).
        var target = _dir.WriteSession("a", Status("thinking", startedAt: 0));
        var store = NewStore();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() => { while (!stop.IsCancellationRequested) store.Poll(); });

        const int writes = 1000;
        int failures = 0;
        for (int i = 1; i <= writes; i++)
        {
            var tmp = System.IO.Path.Combine(_dir.Path, $"a.{i}.tmp");
            _dir.Write(tmp, Status("thinking", startedAt: i));
            try { SharedFile.Replace(tmp, target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
        }
        stop.Cancel();
        await reader;

        Assert.Equal(0, failures);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(writes, Assert.Single(NewStore().Poll()).StartedAt);
    }

    [Fact]
    public async Task Session_files_can_be_deleted_while_the_store_is_polling()
    {
        // lifecycle.js deletes a session's file when it ends; a reader without Delete sharing
        // would make that fail and leave a phantom session behind.
        var store = NewStore();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() => { while (!stop.IsCancellationRequested) store.Poll(); });

        int failures = 0;
        for (int i = 0; i < 300; i++)
        {
            var file = _dir.WriteSession($"s{i}", Status("idle"));
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
        }
        stop.Cancel();
        await reader;

        Assert.Equal(0, failures);
        Assert.Empty(NewStore().Poll());
    }
}
