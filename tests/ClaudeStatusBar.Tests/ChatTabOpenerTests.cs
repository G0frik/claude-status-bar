using Xunit;

namespace ClaudeStatusBar.Tests;

public class ChatTabOpenerTests
{
    private static readonly ChatTab Tab = new(
        @"C:\Program Files\Microsoft VS Code\Code.exe",
        "vscode://anthropic.claude-code/open?session=3f2b8c1e-7a4d-4e9b-9c6a-1d5e0f2a8b47",
        "anthropic.claude-code-2.1.144-win32-x64");

    [Theory]
    [InlineData("ELECTRON_RUN_AS_NODE")]
    [InlineData("electron_run_as_node")]
    [InlineData("VSCODE_IPC_HOOK")]
    [InlineData("VSCODE_PID")]
    [InlineData("VSCODE_ESM_ENTRYPOINT")]
    [InlineData("CLAUDE_CODE_MESSAGING_TOKEN")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    [InlineData("CLAUDE_AGENT_SDK_VERSION")]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_PID")]
    public void Session_variables_are_not_passed_to_the_editor(string name) =>
        Assert.True(EditorLauncher.IsSessionVariable(name));

    [Theory]
    [InlineData("PATH")]
    [InlineData("SYSTEMROOT")]
    [InlineData("USERPROFILE")]
    [InlineData("CLAUDE_CONFIG_DIR")]
    [InlineData("ELECTRONIC")]
    public void Everything_else_is(string name) =>
        Assert.False(EditorLauncher.IsSessionVariable(name));

    [Fact]
    public void The_editor_gets_open_url_and_the_uri_as_separate_arguments_without_a_shell()
    {
        var info = EditorLauncher.StartInfo(Tab);
        Assert.Equal(Tab.EditorExe, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["--open-url", "--", Tab.Uri], info.ArgumentList);
        Assert.Equal(@"C:\Program Files\Microsoft VS Code", info.WorkingDirectory);
    }

    [Fact]
    public void The_editor_environment_is_scrubbed()
    {
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", "1");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN", "secret");
        try
        {
            var env = EditorLauncher.StartInfo(Tab).Environment;
            Assert.False(env.ContainsKey("ELECTRON_RUN_AS_NODE"));
            Assert.False(env.ContainsKey("CLAUDE_CODE_MESSAGING_TOKEN"));
            Assert.True(env.ContainsKey("PATH") || env.ContainsKey("Path"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN", null);
        }
    }

    [Fact]
    public void Debounce_lets_each_key_through_once_per_interval()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var debounce = new Debounce(TimeSpan.FromSeconds(2), () => now);

        Assert.True(debounce.TryEnter("a"));
        Assert.False(debounce.TryEnter("a"));
        Assert.True(debounce.TryEnter("b"));    // another session isn't held up

        now = now.AddSeconds(1.9);
        Assert.False(debounce.TryEnter("a"));
        now = now.AddSeconds(0.2);
        Assert.True(debounce.TryEnter("a"));
    }
}
