using Xunit;

namespace ClaudeStatusBar.Tests;

public class SessionTransitionsTests
{
    private static ClaudeState S(string id, string state, double lastTurn = 0) =>
        new() { SessionId = id, State = state, LastTurnSeconds = lastTurn };

    private static List<(SessionEventKind, string)> Kinds(IReadOnlyList<SessionEvent> events) =>
        events.Select(e => (e.Kind, e.Session.SessionId)).ToList();

    [Fact]
    public void First_sight_of_a_session_fires_nothing()
    {
        var t = new SessionTransitions();
        Assert.Empty(t.Update([S("a", "permission"), S("b", "done", 300)]));
    }

    [Fact]
    public void Permission_request_and_resolution()
    {
        var t = new SessionTransitions();
        t.Update([S("a", "thinking")]);
        Assert.Equal([(SessionEventKind.PermissionRequested, "a")], Kinds(t.Update([S("a", "permission")])));
        Assert.Empty(t.Update([S("a", "permission")]));                       // no repeat while waiting
        Assert.Equal([(SessionEventKind.PermissionResolved, "a")], Kinds(t.Update([S("a", "tool")])));
    }

    [Fact]
    public void Finishing_straight_from_permission_resolves_it_too()
    {
        var t = new SessionTransitions();
        t.Update([S("a", "permission")]);
        Assert.Equal([(SessionEventKind.PermissionResolved, "a"), (SessionEventKind.Finished, "a")],
                     Kinds(t.Update([S("a", "done", 40)])));
    }

    [Fact]
    public void Each_session_is_tracked_separately()
    {
        var t = new SessionTransitions();
        t.Update([S("a", "thinking"), S("b", "thinking")]);
        Assert.Equal([(SessionEventKind.Finished, "b")], Kinds(t.Update([S("a", "thinking"), S("b", "done", 30)])));
    }

    [Fact]
    public void A_session_that_disappears_ends_once()
    {
        var t = new SessionTransitions();
        t.Update([S("a", "permission"), S("b", "idle")]);
        Assert.Equal([(SessionEventKind.Ended, "a")], Kinds(t.Update([S("b", "idle")])));
        Assert.Empty(t.Update([S("b", "idle")]));
    }
}

public class NotificationPolicyTests
{
    private static Settings On() => new() { ShowNotifications = true, NotifyPermission = true, NotifyFinished = true };
    private static SessionEvent E(SessionEventKind kind, double lastTurn = 0) =>
        new(kind, new ClaudeState { SessionId = "a", LastTurnSeconds = lastTurn });

    [Fact]
    public void Permission_requests_notify()
    {
        Assert.True(NotificationPolicy.ShouldShow(E(SessionEventKind.PermissionRequested), On(), userBusy: false, lookingAtSession: false));
    }

    [Theory]
    [InlineData(19.9, false)]
    [InlineData(20, true)]
    [InlineData(600, true)]
    public void Only_long_turns_notify_when_finished(double seconds, bool expected)
    {
        Assert.Equal(expected, NotificationPolicy.ShouldShow(E(SessionEventKind.Finished, seconds), On(), false, false));
    }

    [Fact]
    public void Nothing_while_busy_or_already_looking_at_the_session()
    {
        var e = E(SessionEventKind.PermissionRequested);
        Assert.False(NotificationPolicy.ShouldShow(e, On(), userBusy: true, lookingAtSession: false));
        Assert.False(NotificationPolicy.ShouldShow(e, On(), userBusy: false, lookingAtSession: true));
    }

    [Fact]
    public void Settings_switch_each_kind_and_all_of_them()
    {
        var permission = E(SessionEventKind.PermissionRequested);
        var finished = E(SessionEventKind.Finished, 60);

        var off = On(); off.ShowNotifications = false;
        Assert.False(NotificationPolicy.ShouldShow(permission, off, false, false));
        Assert.False(NotificationPolicy.ShouldShow(finished, off, false, false));

        var noPermission = On(); noPermission.NotifyPermission = false;
        Assert.False(NotificationPolicy.ShouldShow(permission, noPermission, false, false));
        Assert.True(NotificationPolicy.ShouldShow(finished, noPermission, false, false));

        var noFinished = On(); noFinished.NotifyFinished = false;
        Assert.True(NotificationPolicy.ShouldShow(permission, noFinished, false, false));
        Assert.False(NotificationPolicy.ShouldShow(finished, noFinished, false, false));
    }

    [Fact]
    public void Resolution_and_end_events_never_notify_but_dismiss()
    {
        foreach (var kind in new[] { SessionEventKind.PermissionResolved, SessionEventKind.Ended })
        {
            Assert.False(NotificationPolicy.ShouldShow(E(kind), On(), false, false));
            Assert.True(NotificationPolicy.Dismisses(kind));
        }
        Assert.False(NotificationPolicy.Dismisses(SessionEventKind.PermissionRequested));
        Assert.False(NotificationPolicy.Dismisses(SessionEventKind.Finished));
    }
}

public class ToastLayoutTests
{
    [Fact]
    public void Stacks_upwards_from_the_pill_newest_nearest()
    {
        var tops = ToastLayout.Tops(anchorTop: 1000, [50, 40, 60]);
        Assert.Equal([1000 - 6 - 50, 1000 - 6 - 50 - 6 - 40, 1000 - 6 - 50 - 6 - 40 - 6 - 60], tops);
    }

    [Fact]
    public void Nothing_to_stack()
    {
        Assert.Empty(ToastLayout.Tops(1000, []));
    }
}

public class ToastPlacementTests
{
    [Theory]
    [InlineData(1400, 247)]   // one session with usage bars
    [InlineData(1360, 287)]   // five sessions: dots widen the pill
    public void Notifications_take_the_pills_width_and_edges(double pillLeft, double pillWidth)
    {
        var placed = ToastLayout.Place(pillLeft, pillWidth, pillTop: 1043, count: 2, height: 52);
        Assert.All(placed, p => { Assert.Equal(pillLeft, p.Left); Assert.Equal(pillWidth, p.Width); });
        Assert.Equal(1043 - 6 - 52, placed[0].Top);
        Assert.Equal(1043 - 6 - 52 - 6 - 52, placed[1].Top);
    }

    [Fact]
    public void An_unmeasured_or_tiny_pill_still_gets_a_readable_width_aligned_right()
    {
        var placed = ToastLayout.Place(pillLeft: 1500, pillWidth: 0, pillTop: 1043, count: 1, height: 52);
        Assert.Equal(ToastLayout.MinWidth, placed[0].Width);
        Assert.Equal(1500 - ToastLayout.MinWidth, placed[0].Left); // right edge stays at the pill's
    }
}
