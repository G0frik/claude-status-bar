using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStatusBar;

public enum AnimStyle { Spark, Code, Crab }

/// <summary>
/// User preferences, persisted as JSON under the status-bar data directory. A single shared
/// instance backs all overlay windows so a change made on one monitor is visible to the others.
/// </summary>
public sealed class Settings
{
    public bool ShowTimer { get; set; } = true;
    public bool PlayCompletionSound { get; set; } = false;
    public bool ShowUsageBars { get; set; } = true;

#if !OFFLINE
    /// <summary>Fetch usage from Anthropic's usage endpoint; when off, only local sources are used.</summary>
    public bool FetchUsageOnline { get; set; } = true;

    /// <summary>Minutes between online usage fetches while Claude Code is active.</summary>
    public int UsageRefreshMinutes { get; set; } = 5;
#endif

    /// <summary>Show notifications at all; the two below choose which.</summary>
    public bool ShowNotifications { get; set; } = true;
    public bool NotifyPermission { get; set; } = true;
    public bool NotifyFinished { get; set; } = true;

    /// <summary>Monitor device names to show the widget on. Empty means the primary monitor only.</summary>
    public List<string> Monitors { get; set; } = new();

    public AnimStyle Style { get; set; } = AnimStyle.Spark;

    private static readonly string Path = System.IO.Path.Combine(StateStore.Dir, "win-settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Shared { get; } = Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), Options) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StateStore.Dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}
