using Xunit;

namespace ClaudeStatusBar.Tests;

public class SessionFocusTests
{
    private static ClaudeState S(string id, string state, double ts = 0) => new() { SessionId = id, State = state, Ts = ts };

    [Fact]
    public void No_sessions_means_no_focus() => Assert.Null(SessionFocus.Pick([], null));

    [Fact]
    public void Single_session_is_focused_whatever_its_state()
    {
        Assert.Equal("a", SessionFocus.Pick([S("a", "idle")], null)!.SessionId);
        Assert.Equal("a", SessionFocus.Pick([S("a", "idle")], "gone")!.SessionId);
    }

    [Theory]
    [InlineData("permission", "thinking")]
    [InlineData("permission", "tool")]
    [InlineData("thinking", "done")]
    [InlineData("tool", "idle")]
    [InlineData("done", "idle")]
    public void Higher_priority_state_wins(string winner, string loser)
    {
        var sessions = new[] { S("lo", loser, ts: 100), S("hi", winner, ts: 1) };
        Assert.Equal("hi", SessionFocus.Pick(sessions, null)!.SessionId);
        Assert.Equal("hi", SessionFocus.Pick(sessions, "lo")!.SessionId); // even against current focus
    }

    [Fact]
    public void Working_focus_is_sticky_when_another_session_starts_working()
    {
        var sessions = new[] { S("first", "thinking", ts: 10), S("newer", "tool", ts: 20) };
        Assert.Equal("first", SessionFocus.Pick(sessions, "first")!.SessionId);
    }

    [Fact]
    public void Without_a_current_focus_the_most_recent_in_the_top_class_wins()
    {
        var sessions = new[] { S("a", "thinking", ts: 10), S("b", "tool", ts: 20), S("c", "idle", ts: 30) };
        Assert.Equal("b", SessionFocus.Pick(sessions, null)!.SessionId);
    }

    [Fact]
    public void Focus_moves_on_when_the_focused_session_finishes()
    {
        var sessions = new[] { S("first", "done", ts: 30), S("other", "thinking", ts: 20) };
        Assert.Equal("other", SessionFocus.Pick(sessions, "first")!.SessionId);
    }

    [Fact]
    public void Permission_request_takes_focus_and_gives_it_back_afterwards()
    {
        string? focus = "a";
        focus = SessionFocus.Pick([S("a", "thinking", 10), S("b", "permission", 20)], focus)!.SessionId;
        Assert.Equal("b", focus);
        // b approved and working again: both working, b keeps focus (sticky) rather than flicking back.
        focus = SessionFocus.Pick([S("a", "thinking", 10), S("b", "tool", 30)], focus)!.SessionId;
        Assert.Equal("b", focus);
    }

    [Fact]
    public void Focused_session_that_disappeared_is_replaced()
    {
        var sessions = new[] { S("a", "idle", ts: 5), S("b", "idle", ts: 9) };
        Assert.Equal("b", SessionFocus.Pick(sessions, "ended")!.SessionId);
    }

    [Fact]
    public void Ranks_cover_every_state()
    {
        Assert.Equal(3, SessionFocus.Rank("permission"));
        Assert.Equal(2, SessionFocus.Rank("thinking"));
        Assert.Equal(2, SessionFocus.Rank("tool"));
        Assert.Equal(1, SessionFocus.Rank("done"));
        Assert.Equal(0, SessionFocus.Rank("idle"));
        Assert.Equal(0, SessionFocus.Rank("something-new"));
    }
}

public class SessionNamesTests
{
    private static ClaudeState S(string id, string project) => new() { SessionId = id, Project = project };

    [Fact]
    public void Uses_project_names_and_numbers_duplicates_in_order()
    {
        var names = SessionNames.Build([S("1", "api"), S("2", "web"), S("3", "api"), S("4", "API")]);
        Assert.Equal("api", names["1"]);
        Assert.Equal("web", names["2"]);
        Assert.Equal("api · 2", names["3"]);
        Assert.Equal("API · 3", names["4"]);
    }

    [Fact]
    public void Blank_project_falls_back_to_Claude()
    {
        var names = SessionNames.Build([S("1", ""), S("2", "  ")]);
        Assert.Equal("Claude", names["1"]);
        Assert.Equal("Claude · 2", names["2"]);
    }
}
