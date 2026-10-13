using System.IO;
using System.Text.Json;

namespace ClaudeStatusBar.Tests;

/// <summary>A throwaway status-bar data directory (with sessions.d) for one test.</summary>
public sealed class TestDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csb-tests", Guid.NewGuid().ToString("N"));
    public string Sessions => System.IO.Path.Combine(Path, "sessions.d");

    public TestDir() => Directory.CreateDirectory(Sessions);

    public string WriteSession(string id, object status) => Write(System.IO.Path.Combine(Sessions, id), status);

    public string Write(string file, object content)
    {
        File.WriteAllText(file, content as string ?? JsonSerializer.Serialize(content));
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
