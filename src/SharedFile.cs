using System.IO;

namespace ClaudeStatusBar;

/// <summary>
/// Access to files that several processes share: the hooks replace them atomically (write a temp
/// file, rename it over the target) and delete session files, while the widget reads them.
///
/// On Windows a rename onto a file fails with "access denied" while any process has it open, even
/// briefly and even with Delete sharing, so writers must retry (<see cref="Replace"/>, and
/// hooks/atomic.js for the hooks). Reads allow Delete sharing so that a hook deleting a session
/// file is never blocked by the widget.
/// </summary>
public static class SharedFile
{
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream); // UTF-8, honoring a BOM if present
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Move <paramref name="source"/> over <paramref name="target"/>, retrying for up to about a
    /// second while another process holds the target open. Throws if it still fails.
    /// </summary>
    public static void Replace(string source, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(5 * attempt);
            }
        }
    }
}
