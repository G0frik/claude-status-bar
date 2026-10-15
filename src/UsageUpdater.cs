using System.IO;
using System.Text.Json;

namespace ClaudeStatusBar;

/// <summary>An extra usage source polled on each tick: the online build's usage API fetch.</summary>
public interface IUsageSource
{
    /// <summary>Do this tick's work, writing results through <paramref name="output"/>. Handles its own errors.</summary>
    Task TickAsync(UsageFile output, CancellationToken ct);

    /// <summary>Allow an immediate update (after a settings change).</summary>
    void Refresh();
}

/// <summary>
/// Keeps usage.json current for clients that never run the statusLine command, such as the VS Code
/// extension; statusline.js writes the same file from the CLI.
///
/// Every 15 s it reads the copy of the usage response that Claude Code caches in ~/.claude.json
/// (no network) and uses it whenever it is newer than what usage.json holds. That copy only updates
/// when Claude Code itself fetches usage, so on its own it can be hours old. Of ~/.claude.json only
/// the cachedUsageUtilization key is used. An optional <see cref="IUsageSource"/> runs on the same
/// tick.
/// </summary>
public sealed class UsageUpdater : IDisposable
{
    public static UsageUpdater? Instance { get; private set; }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15); // cheap: a file timestamp check

    private readonly string _claudeJsonPath;
    private readonly UsageFile _output;
    private readonly IUsageSource? _source;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTime _cacheWrite = DateTime.MinValue;

    /// <param name="source">Extra source polled each tick; null uses only local sources.</param>
    /// <param name="home">Folder holding .claude.json; defaults to the user profile.</param>
    /// <param name="output">Where results go; defaults to the real usage.json and log.</param>
    public UsageUpdater(IUsageSource? source = null, string? home = null, UsageFile? output = null)
    {
        Instance = this;
        _source = source;
        _claudeJsonPath = Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        _output = output ?? new UsageFile();
    }

    public void Start() => _ = RunAsync(_cts.Token);

    public void Refresh()
    {
        _source?.Refresh();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void Dispose()
    {
        _cts.Cancel();
        (_source as IDisposable)?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

            try { await _wake.WaitAsync(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        ReadCacheIfChanged();
        if (_source is null)
            _output.Status("cache", "using Claude Code's cached usage in ~/.claude.json and the terminal statusLine");
        else
            await _source.TickAsync(_output, ct);
    }

    private void ReadCacheIfChanged()
    {
        try
        {
            var info = new FileInfo(_claudeJsonPath);
            if (!info.Exists || info.LastWriteTimeUtc == _cacheWrite) return;

            using var doc = JsonDocument.Parse(SharedFile.ReadAllText(_claudeJsonPath));
            _cacheWrite = info.LastWriteTimeUtc; // only after a successful read, so a failed one retries
            if (!doc.RootElement.TryGetProperty("cachedUsageUtilization", out var cache) ||
                !cache.TryGetProperty("fetchedAtMs", out var at) || at.ValueKind != JsonValueKind.Number ||
                !cache.TryGetProperty("utilization", out var utilization) ||
                UsageFile.Parse(utilization) is not { } usage)
                return;

            long fetchedAt = (long)(at.GetDouble() / 1000);
            if (fetchedAt > _output.ReadTimestamp()) _output.Write(usage, "cache", fetchedAt);
        }
        catch { } // mid-write by Claude Code; try again next tick
    }
}
