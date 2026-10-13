using System.IO;

namespace ClaudeStatusBar;

/// <summary>
/// Plain-text log of where the usage bars' numbers come from: API fetch results and each change
/// the widget displays. Written next to the executable as usage.log, and started afresh once it
/// grows past a size cap.
/// </summary>
public static class UsageLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "usage.log");
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
