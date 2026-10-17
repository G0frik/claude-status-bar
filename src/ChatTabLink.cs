using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeStatusBar;

/// <summary>A link that switches an editor to a session's chat tab: the editor exe to hand it to, and the URI.</summary>
/// <param name="Extension">The Claude Code extension's folder name (it carries the version), for the log.</param>
public sealed record ChatTab(string EditorExe, string Uri, string Extension);

/// <summary>The files the link check reads; faked in tests.</summary>
public interface IEditorFiles
{
    /// <summary>The text of the editor's product.json (resources\app, next to its exe), or null.</summary>
    string? ProductJson(string editorExe);

    /// <summary>Whether the Claude Code extension in this folder handles the /open URI.</summary>
    bool HasOpenRoute(string extensionDir);
}

/// <summary>
/// Decides whether a click can also switch the editor to the session's own chat tab, through the
/// Claude Code extension's URI route <c>&lt;scheme&gt;://anthropic.claude-code/open?session=&lt;id&gt;</c>.
///
/// Only sessions running in the extension's panel qualify: their process is the extension's
/// bundled claude.exe and its parent is the editor itself (the extension host). A terminal
/// session's parent is a shell; sending it the link would open a second copy of the session in
/// a panel. The URI scheme comes from the editor's own product.json, so any VS Code-based editor
/// works without a list. This is the pure part; <see cref="ChatTabOpener"/> feeds it live data.
/// </summary>
public static class ChatTabLink
{
    public const string Authority = "anthropic.claude-code";

    private static readonly Regex BundledClaude = new(
        @"^(?<ext>.*[\\/]anthropic\.claude-code-[^\\/]+)[\\/]resources[\\/]native-binary[\\/]claude(\.exe)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // RFC 3986 scheme syntax, lower case, short: nothing that could change the URI's meaning.
    private static readonly Regex SchemeSyntax = new(@"^[a-z][a-z0-9+.-]{0,31}$", RegexOptions.CultureInvariant);

    private static readonly Regex OpenCase = new(@"case\s*[""'`]/open[""'`]", RegexOptions.CultureInvariant);

    /// <param name="sessionExe">Image path of the session's process.</param>
    /// <param name="parentExe">Image path of its parent.</param>
    /// <param name="ownerExe">Image path of the process owning the session's window.</param>
    /// <returns>The link, or null with the reason it was skipped.</returns>
    public static (ChatTab? Tab, string Reason) Plan(string sessionId, string? sessionExe, string? parentExe, string? ownerExe, IEditorFiles files)
    {
        if (!Guid.TryParseExact(sessionId, "D", out var id)) return (null, "bad-id");
        if (sessionExe is null || parentExe is null || ownerExe is null) return (null, "no-process");

        var bundled = BundledClaude.Match(sessionExe);
        if (!bundled.Success || !string.Equals(parentExe, ownerExe, StringComparison.OrdinalIgnoreCase))
            return (null, "not-panel");

        var scheme = UrlProtocol(files.ProductJson(ownerExe));
        if (scheme is null) return (null, "no-protocol");

        var extensionDir = bundled.Groups["ext"].Value;
        if (!files.HasOpenRoute(extensionDir)) return (null, "no-open-route");

        // Never a prompt (it would be typed into the session) and never a windowId (routing is ours).
        var uri = $"{scheme}://{Authority}/open?session={Uri.EscapeDataString(id.ToString("D"))}";
        return (new ChatTab(ownerExe, uri, Path.GetFileName(extensionDir)), "ok");
    }

    /// <summary>The editor's URL scheme from its product.json, or null if missing or not a plain scheme.</summary>
    public static string? UrlProtocol(string? productJson)
    {
        if (string.IsNullOrWhiteSpace(productJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(productJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("urlProtocol", out var value)
                || value.ValueKind != JsonValueKind.String) return null;
            var scheme = value.GetString()!;
            return SchemeSyntax.IsMatch(scheme) ? scheme : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether an extension.js registers a URI handler whose /open route runs
    /// claude-vscode.primaryEditor.open: the contract this feature relies on (checked against the
    /// Marketplace build by the extension-contract workflow).
    /// </summary>
    public static bool HasOpenRoute(string extensionJs) =>
        extensionJs.Contains("registerUriHandler", StringComparison.Ordinal)
        && OpenCase.IsMatch(extensionJs)
        && extensionJs.Contains("\"claude-vscode.primaryEditor.open\"", StringComparison.Ordinal);
}

/// <summary>
/// Reads the installed editor's and extension's files. The route check reads a few MB of
/// extension.js, so its answer is kept per extension folder; an update installs a new folder.
/// </summary>
public sealed class InstalledEditorFiles : IEditorFiles
{
    public static InstalledEditorFiles Instance { get; } = new();

    private readonly Dictionary<string, bool> _routes = new(StringComparer.OrdinalIgnoreCase);

    public string? ProductJson(string editorExe)
    {
        try
        {
            var dir = Path.GetDirectoryName(editorExe);
            if (dir is null) return null;
            var path = Path.Combine(dir, "resources", "app", "product.json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    public bool HasOpenRoute(string extensionDir)
    {
        lock (_routes)
            if (_routes.TryGetValue(extensionDir, out bool known)) return known;

        bool has;
        try { has = ChatTabLink.HasOpenRoute(File.ReadAllText(Path.Combine(extensionDir, "extension.js"))); }
        catch { return false; } // unreadable now; try again next time

        lock (_routes) _routes[extensionDir] = has;
        return has;
    }
}
