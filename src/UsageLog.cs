using System.IO;

namespace ClaudeStatusBar;

/// <summary>
/// Plain-text log of where the usage bars' numbers come from: API fetch results and each change
/// the widget displays. Written next to the executable as usage.log.
/// </summary>
public static class UsageLog
{
    private static readonly CappedLog Log = new("usage.log");

    public static void Write(string line) => Log.Write(line);
}

/// <summary>A timestamped plain-text log next to the executable, started afresh once it grows past a size cap.</summary>
public sealed class CappedLog(string fileName)
{
    private const long MaxBytes = 512 * 1024;

    private readonly string _path = Path.Combine(AppContext.BaseDirectory, fileName);
    private readonly object _gate = new();

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) info.Delete();
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
