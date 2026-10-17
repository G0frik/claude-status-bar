using System.IO;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class ChatTabLinkTests
{
    private const string Id = "3f2b8c1e-7a4d-4e9b-9c6a-1d5e0f2a8b47";

    // Paths as observed for a live session in each editor.
    private const string Antigravity = @"C:\Users\me\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe";
    private const string AntigravityClaude = @"c:\Users\me\.antigravity-ide\extensions\anthropic.claude-code-2.1.296-win32-x64\resources\native-binary\claude.exe";
    private const string VsCode = @"C:\Program Files\Microsoft VS Code\Code.exe";
    private const string VsCodeClaude = @"C:\Users\me\.vscode\extensions\anthropic.claude-code-2.1.144-win32-x64\resources\native-binary\claude.exe";

    private sealed class Files(Dictionary<string, string>? products = null, bool route = true) : IEditorFiles
    {
        public readonly List<string> RouteChecks = new();

        public string? ProductJson(string editorExe) =>
            (products ?? Default).GetValueOrDefault(editorExe);

        public bool HasOpenRoute(string extensionDir) { RouteChecks.Add(extensionDir); return route; }

        private static readonly Dictionary<string, string> Default = new()
        {
            [Antigravity] = """{"nameShort":"Antigravity IDE","urlProtocol":"antigravity-ide"}""",
            [VsCode] = """{"nameShort":"Code","urlProtocol":"vscode"}""",
        };
    }

    [Fact]
    public void Antigravity_panel_session_links_to_its_own_scheme()
    {
        var files = new Files();
        var (tab, reason) = ChatTabLink.Plan(Id, AntigravityClaude, Antigravity, Antigravity, files);
        Assert.Equal("ok", reason);
        Assert.Equal(Antigravity, tab!.EditorExe);
        Assert.Equal($"antigravity-ide://anthropic.claude-code/open?session={Id}", tab.Uri);
        Assert.Equal("anthropic.claude-code-2.1.296-win32-x64", tab.Extension);
        Assert.Equal(@"c:\Users\me\.antigravity-ide\extensions\anthropic.claude-code-2.1.296-win32-x64", Assert.Single(files.RouteChecks));
    }

    [Fact]
    public void VS_Code_panel_session_links_to_vscode()
    {
        var (tab, _) = ChatTabLink.Plan(Id, VsCodeClaude, VsCode, VsCode, new Files());
        Assert.Equal($"vscode://anthropic.claude-code/open?session={Id}", tab!.Uri);
    }

    [Fact]
    public void Any_fork_works_from_its_product_json_without_a_list()
    {
        const string fork = @"D:\Tools\SomeFork\SomeFork.exe";
        var files = new Files(new() { [fork] = """{"urlProtocol":"some-fork"}""" });
        var (tab, _) = ChatTabLink.Plan(Id, @"D:\data\.some-fork\extensions\anthropic.claude-code-3.0.0\resources\native-binary\claude.exe", fork, fork, files);
        Assert.Equal($"some-fork://anthropic.claude-code/open?session={Id}", tab!.Uri);
    }

    [Theory]
    // CLI installed on its own, in the editor's integrated terminal (parent: a shell).
    [InlineData(@"C:\Users\me\.local\bin\claude.exe", @"C:\Program Files\PowerShell\7\pwsh.exe")]
    // CLI in Windows Terminal.
    [InlineData(@"C:\Users\me\.local\bin\claude.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    // The extension's own binary run in a terminal ("use terminal" mode): still a shell parent.
    [InlineData(AntigravityClaude, @"C:\Program Files\Git\usr\bin\bash.exe")]
    // The CLI under the editor exe but not the bundled binary (e.g. node-based install).
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\claude.exe", Antigravity)]
    public void Terminal_sessions_are_not_linked(string sessionExe, string parentExe)
    {
        var (tab, reason) = ChatTabLink.Plan(Id, sessionExe, parentExe, Antigravity, new Files());
        Assert.Null(tab);
        Assert.Equal("not-panel", reason);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(Id + "&prompt=rm -rf")]
    [InlineData("{" + Id + "}")]
    [InlineData("3f2b8c1e7a4d4e9b9c6a1d5e0f2a8b47")]
    public void Anything_but_a_plain_GUID_is_refused(string sessionId)
    {
        var (tab, reason) = ChatTabLink.Plan(sessionId, AntigravityClaude, Antigravity, Antigravity, new Files());
        Assert.Null(tab);
        Assert.Equal("bad-id", reason);
    }

    [Fact]
    public void An_upper_case_id_is_normalised()
    {
        var (tab, _) = ChatTabLink.Plan(Id.ToUpperInvariant(), AntigravityClaude, Antigravity, Antigravity, new Files());
        Assert.EndsWith($"?session={Id}", tab!.Uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"nameShort":"Code"}""")]
    [InlineData("""{"urlProtocol":42}""")]
    [InlineData("""{"urlProtocol":"vscode://evil"}""")]
    [InlineData("""{"urlProtocol":"bad scheme"}""")]
    [InlineData("""{"urlProtocol":"VSCode"}""")]
    [InlineData("""{"urlProtocol":"1code"}""")]
    public void A_missing_or_odd_url_protocol_is_skipped(string? productJson)
    {
        var products = new Dictionary<string, string>();
        if (productJson is not null) products[Antigravity] = productJson;
        var (tab, reason) = ChatTabLink.Plan(Id, AntigravityClaude, Antigravity, Antigravity, new Files(products));
        Assert.Null(tab);
        Assert.Equal("no-protocol", reason);
    }

    [Fact]
    public void An_extension_without_the_open_route_is_skipped()
    {
        var (tab, reason) = ChatTabLink.Plan(Id, AntigravityClaude, Antigravity, Antigravity, new Files(route: false));
        Assert.Null(tab);
        Assert.Equal("no-open-route", reason);
    }

    [Fact]
    public void Unknown_process_paths_are_skipped()
    {
        Assert.Equal("no-process", ChatTabLink.Plan(Id, null, Antigravity, Antigravity, new Files()).Reason);
        Assert.Equal("no-process", ChatTabLink.Plan(Id, AntigravityClaude, null, Antigravity, new Files()).Reason);
        Assert.Equal("no-process", ChatTabLink.Plan(Id, AntigravityClaude, Antigravity, null, new Files()).Reason);
    }

    [Fact]
    public void The_link_never_carries_a_prompt_or_a_window_id()
    {
        var (tab, _) = ChatTabLink.Plan(Id, AntigravityClaude, Antigravity, Antigravity, new Files());
        Assert.DoesNotContain("prompt", tab!.Uri);
        Assert.DoesNotContain("windowId", tab.Uri);
        Assert.Equal($"?session={Id}", new Uri(tab.Uri).Query);
    }

    // ----- the /open route contract -----

    // The handler as minified in extension.js 2.1.296.
    private const string HandlerSnippet =
        """$.subscriptions.push(w1.window.registerUriHandler({handleUri(_){let x=new URLSearchParams(_.query);switch(_.path){case"/install-plugin":{return}case"/open":{let R=x.get("session")??void 0,A=x.get("prompt")??void 0;if(R!==void 0&&!bZ(R))return;w1.commands.executeCommand("claude-vscode.primaryEditor.open",R,A);return}}}}))""";

    [Fact]
    public void The_route_probe_recognises_the_shipped_handler()
    {
        Assert.True(ChatTabLink.HasOpenRoute(HandlerSnippet));
        Assert.True(ChatTabLink.HasOpenRoute("registerUriHandler({handleUri(u) { switch (u.path) { case '/open': commands.executeCommand(\"claude-vscode.primaryEditor.open\", s) } }})"));
    }

    [Theory]
    [InlineData("registerUriHandler")]                                    // no /open route
    [InlineData("case\"/open\":{executeCommand(\"claude-vscode.primaryEditor.open\")}")] // no URI handler
    [InlineData("registerUriHandler({handleUri(u){switch(u.path){case\"/open\":{executeCommand(\"claude-vscode.editor.open\")}}}})")] // command renamed
    [InlineData("registerUriHandler({handleUri(u){switch(u.path){case\"/opened\":{executeCommand(\"claude-vscode.primaryEditor.open\")}}}})")]
    public void The_route_probe_rejects_extensions_without_the_contract(string js) =>
        Assert.False(ChatTabLink.HasOpenRoute(js));

    /// <summary>
    /// The contract against a real extension.js: CI's extension-contract workflow points
    /// CLAUDE_EXT_JS at the one in the current Marketplace build. Without it there is nothing to check.
    /// </summary>
    [Fact]
    public void The_extension_named_by_CLAUDE_EXT_JS_still_has_the_open_route()
    {
        var path = Environment.GetEnvironmentVariable("CLAUDE_EXT_JS");
        if (string.IsNullOrEmpty(path)) return;
        Assert.True(File.Exists(path), $"CLAUDE_EXT_JS points at a missing file: {path}");
        Assert.True(ChatTabLink.HasOpenRoute(File.ReadAllText(path)),
            "The Claude Code extension no longer registers /open → claude-vscode.primaryEditor.open; opening a chat tab will be skipped.");
    }

    [Fact]
    public void Installed_files_cache_the_route_per_extension_folder()
    {
        using var dir = new TestDir();
        var ext = Path.Combine(dir.Path, "anthropic.claude-code-1.0.0");
        Directory.CreateDirectory(ext);
        File.WriteAllText(Path.Combine(ext, "extension.js"), HandlerSnippet);
        var files = new InstalledEditorFiles();
        Assert.True(files.HasOpenRoute(ext));

        File.WriteAllText(Path.Combine(ext, "extension.js"), "nothing here");
        Assert.True(files.HasOpenRoute(ext)); // same folder = same version: the answer is kept
        Assert.False(files.HasOpenRoute(Path.Combine(dir.Path, "missing"))); // unreadable: false, not cached
    }

    [Fact]
    public void Installed_files_read_product_json_next_to_the_exe()
    {
        using var dir = new TestDir();
        var app = Path.Combine(dir.Path, "resources", "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "product.json"), """{"urlProtocol":"x-editor"}""");
        var files = new InstalledEditorFiles();
        Assert.Equal("x-editor", ChatTabLink.UrlProtocol(files.ProductJson(Path.Combine(dir.Path, "Editor.exe"))));
        Assert.Null(files.ProductJson(Path.Combine(dir.Path, "sub", "Editor.exe")));
    }
}
