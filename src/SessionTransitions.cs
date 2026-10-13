namespace ClaudeStatusBar;

public enum SessionEventKind
{
    /// <summary>The session started waiting for permission.</summary>
    PermissionRequested,
    /// <summary>The session stopped waiting for permission (approved, denied, or moved on).</summary>
    PermissionResolved,
    /// <summary>The session finished a turn (any length; see <see cref="NotificationPolicy"/>).</summary>
    Finished,
    /// <summary>The session is gone.</summary>
    Ended,
}

public sealed record SessionEvent(SessionEventKind Kind, ClaudeState Session);

/// <summary>
/// Turns successive polls of the sessions into events. A session seen for the first time
/// produces none, so starting the widget never fires a burst of stale notifications.
/// </summary>
public sealed class SessionTransitions
{
    private readonly Dictionary<string, ClaudeState> _last = new();

    public IReadOnlyList<SessionEvent> Update(IReadOnlyList<ClaudeState> sessions)
    {
        List<SessionEvent>? events = null;
        void Add(SessionEventKind kind, ClaudeState s) => (events ??= new()).Add(new SessionEvent(kind, s));

        foreach (var s in sessions)
        {
            if (_last.TryGetValue(s.SessionId, out var prev) && prev.State != s.State)
            {
                if (prev.State == "permission") Add(SessionEventKind.PermissionResolved, s);
                if (s.State == "permission") Add(SessionEventKind.PermissionRequested, s);
                if (s.State == "done") Add(SessionEventKind.Finished, s);
            }
            _last[s.SessionId] = s;
        }

        if (_last.Count > sessions.Count)
        {
            var live = new HashSet<string>(sessions.Select(s => s.SessionId));
            foreach (var gone in _last.Keys.Where(k => !live.Contains(k)).ToList())
            {
                Add(SessionEventKind.Ended, _last[gone]);
                _last.Remove(gone);
            }
        }
        return (IReadOnlyList<SessionEvent>?)events ?? [];
    }
}

/// <summary>Which events become a notification.</summary>
public static class NotificationPolicy
{
    /// <summary>Turns shorter than this finish quietly: you were most likely still watching.</summary>
    public const double FinishedMinSeconds = 20;

    /// <param name="userBusy">Windows reports a fullscreen app, presentation mode or quiet hours.</param>
    /// <param name="lookingAtSession">The session's window is the foreground window.</param>
    public static bool ShouldShow(SessionEvent e, Settings settings, bool userBusy, bool lookingAtSession)
    {
        if (!settings.ShowNotifications || userBusy || lookingAtSession) return false;
        return e.Kind switch
        {
            SessionEventKind.PermissionRequested => settings.NotifyPermission,
            SessionEventKind.Finished => settings.NotifyFinished && e.Session.LastTurnSeconds >= FinishedMinSeconds,
            _ => false,
        };
    }

    /// <summary>Events that take an existing notification for the session away.</summary>
    public static bool Dismisses(SessionEventKind kind) =>
        kind is SessionEventKind.PermissionResolved or SessionEventKind.Ended;
}

/// <summary>
/// Where stacked notifications go: above the pill, as wide as it and aligned with it (so they
/// follow it as session dots and usage bars come and go), newest nearest to it.
/// </summary>
public static class ToastLayout
{
    public const int MaxToasts = 3;
    public const double Gap = 6;

    /// <summary>Narrowest a notification gets, e.g. before the pill has been measured.</summary>
    public const double MinWidth = 220;

    /// <summary>Bounds for <paramref name="count"/> notifications of equal height, newest first. All values in DIPs.</summary>
    public static (double Left, double Top, double Width)[] Place(double pillLeft, double pillWidth, double pillTop, int count, double height)
    {
        double width = Math.Max(pillWidth, MinWidth);
        double left = pillLeft + pillWidth - width; // right edges line up even when clamped
        var tops = Tops(pillTop, Enumerable.Repeat(height, count).ToList());
        return tops.Select(top => (left, top, width)).ToArray();
    }

    /// <summary>
    /// Top edges for toasts of the given heights, ordered newest first, stacked upwards from
    /// <paramref name="anchorTop"/> (the top of the pill). All values in DIPs.
    /// </summary>
    public static double[] Tops(double anchorTop, IReadOnlyList<double> heightsNewestFirst)
    {
        var tops = new double[heightsNewestFirst.Count];
        double bottom = anchorTop - Gap;
        for (int i = 0; i < tops.Length; i++)
        {
            tops[i] = bottom - heightsNewestFirst[i];
            bottom = tops[i] - Gap;
        }
        return tops;
    }
}
