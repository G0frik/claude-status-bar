using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeStatusBar;

/// <summary>
/// A borderless overlay docked into one monitor's taskbar. Polls the hook-written session
/// statuses, renders the focused session's icon/label/timer (plus one dot per session when
/// several run), and positions itself just before that monitor's tray cluster.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly Color Brand = Color.FromRgb(0xD9, 0x77, 0x57); // Anthropic accent
    private static readonly Color Amber = Color.FromRgb(0xF2, 0xBA, 0x2E); // awaiting-permission dot

    private const double TrayMarginDip = 8;   // gap between the widget and the tray cluster
    private const double VerticalInset = 6;    // total top+bottom gap inside the taskbar band
    private const double VerbRotateSeconds = 4;

    private const double UsageBarHeight = 18;   // matches the icon
    private const double UsageWarnPercent = 80; // bars switch to the accent color at this usage

    private const int MaxDots = 5;              // more sessions show MaxDots-1 dots and "+N"
    private const double DotSize = 6;

    private static readonly Brush UsageFill = Frozen(Color.FromRgb(0xC0, 0xC0, 0xC0)); // timer text color
    private static readonly Brush UsageWarnFill = Frozen(Brand);
    private static readonly Brush DotPermission = Frozen(Amber);
    private static readonly Brush DotIdle = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DotFocusRing = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));

    private readonly Settings _settings = Settings.Shared;
    private readonly StateStore _store = new();
    private readonly SessionStore _sessions = new();
    private readonly UsageStore _usage = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly string _targetDevice; // monitor device name; empty resolves to primary
    private readonly bool _isController;    // only the controller window owns app shutdown

    private bool _closed;

    // Animation, driven by CompositionTarget.Rendering gated on elapsed time.
    private bool _animating;
    private TimeSpan _lastRenderTime = TimeSpan.Zero;
    private double _frameAccumulator;
    private int _frame;

    private double _startedAt; // turn start (unix seconds), 0 when no timer should show

    private readonly SessionTransitions _transitions = new(); // controller only: chime and notifications
    private ToastManager? _toasts;           // created on the first notification
    private MediaPlayer? _chime;

    private (double left, double top, double height, double right) _lastGeometry = (-1, -1, -1, -1);

    private readonly DateTime _launchedAt = DateTime.UtcNow;
    private DateTime? _idleSince;
    private bool _sawSession;

    private string _verb = "Thinking";
    private double _verbStartedAt;
    private int _verbIndex = -1;

    private UsageLimits? _shownUsage;       // last limits rendered; null while the bars are hidden
    private readonly TextBlock _usageTip = new();

    private IReadOnlyList<ClaudeState> _sessionsNow = [];   // as of the last poll
    private string _notice = "";            // brief message shown in place of the label
    private DateTime _noticeUntil;

    private string? _focusId;               // session the icon/label/timer follow
    private int _dotsSignature;             // what the dots currently show (0: none); rebuilt when it changes
    private SessionsPanel? _panel;          // created on first hover
    private string? _hoveredDotId;
    private readonly SolidColorBrush _dotWorking = new(Brand); // pulses while a working dot shows
    private bool _pulsing;

    // Open tooltips/menu. While any is open the widget doesn't re-assert topmost, which would
    // raise it above its own popup and hide the part that overlaps the pill.
    private int _openPopups;

    public OverlayWindow(string targetDevice = "", bool isController = true)
    {
        _targetDevice = targetDevice ?? "";
        _isController = isController;
        InitializeComponent();
#if !OFFLINE
        AddOnlineUsageMenu();
#endif
        UsageBars.ToolTip = TrackedToolTip(_usageTip);
        SessionDots.MouseEnter += (_, _) => Panel().PointerEntered();
        SessionDots.MouseLeave += (_, _) => { _panel?.PointerLeft(); HoverDot(null); };
        SessionDots.MouseMove += (_, e) => HoverDot((e.OriginalSource as FrameworkElement)?.Tag as string);
        Root.SizeChanged += (_, _) => _toasts?.Layout(); // notifications keep the pill's width
        Menu.Opened += (_, _) => _openPopups++;
        Menu.Closed += (_, _) => PopupClosed();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
    }

    private Color IconColor => Brand;

    private double Fps => _settings.Style switch
    {
        AnimStyle.Spark => 9,
        AnimStyle.Code => 5 * 18 / 3.8,
        AnimStyle.Crab => 12.5,
        _ => 9,
    };

    private int FrameCount => _settings.Style switch
    {
        AnimStyle.Spark => IconRenderer.SparkFrameCount,
        AnimStyle.Code => 5 * 18,
        AnimStyle.Crab => IconRenderer.CrabFrameCount,
        _ => IconRenderer.SparkFrameCount,
    };

    private IntPtr _hwnd;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SyncMenuChecks();
        ApplyWindowStyles();
        AssertTopmost();
        Reposition();

        Closed += OnClosed;

        _poll.Tick += (_, _) => Tick();
        _poll.Start();
        CompositionTarget.Rendering += OnRender;
        Tick();
    }

    // Keep the overlay out of Alt-Tab. The app runs without uiAccess or elevation, so the Start
    // menu and shell flyouts can cover it; AssertTopmost keeps it above ordinary windows.
    private void ApplyWindowStyles()
    {
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    private ToolTip TrackedToolTip(TextBlock content)
    {
        var tip = new ToolTip { Content = content };
        tip.Opened += (_, _) => _openPopups++;
        tip.Closed += (_, _) => PopupClosed();
        return tip;
    }

    private void PopupClosed()
    {
        _openPopups = Math.Max(0, _openPopups - 1);
        if (_openPopups == 0) AssertTopmost();
    }

    private void AssertTopmost()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _poll.Stop();
        CompositionTarget.Rendering -= OnRender;
        _toasts?.DismissAll();
    }

    // ----- status polling -----

    private void Tick()
    {
        if (_closed) return;
        CheckLifecycle();
        UpdateUsage();
        Reposition();
        // Recover z-order each tick (cheap; no flicker since pos/size unchanged), except while a
        // popup is open: that would raise the widget above its own tooltip or menu.
        if (_openPopups == 0) AssertTopmost();

        var sessions = _sessions.Poll();
        if (sessions.Count == 0) sessions = [_store.Poll()]; // hooks without per-session status
        _sessionsNow = sessions;
        var state = SessionFocus.Pick(sessions, _focusId)!;
        if (state.SessionId != _focusId) { _focusId = state.SessionId; _verbStartedAt = 0; }
        _startedAt = state.StartedAt;
        if (_isController) HandleTransitions(sessions);
        UpdateDots(sessions, state);
        if (_panel is { IsOpen: true }) _panel.Refresh(sessions, _focusId); // only fed while visible

        switch (state.State)
        {
            case "thinking":
                Render(ThinkingLabel(), animate: true);
                break;
            case "tool":
                _verbStartedAt = 0;
                Render(string.IsNullOrEmpty(state.Label) ? "Working…" : state.Label, animate: true);
                break;
            case "permission":
                _verbStartedAt = 0;
                _startedAt = 0;
                Render("Awaiting permission", animate: false, dot: true);
                break;
            default:
                _verbStartedAt = 0;
                _startedAt = 0;
                Render("Claude", animate: false);
                break;
        }
        if (DateTime.UtcNow < _noticeUntil) StatusText.Text = _notice;
        UpdateTimer();
    }

    // ----- positioning -----

    private double DipScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
    }

    /// <summary>Dock into the target monitor's horizontal taskbar band, anchored before the tray.</summary>
    private void Reposition()
    {
        if (_closed) return;

        var monitors = Native.GetMonitors();
        var mon = monitors.FirstOrDefault(m => m.Device == _targetDevice)
                  ?? monitors.FirstOrDefault(m => m.Primary)
                  ?? monitors.FirstOrDefault();
        if (mon is null) { Visibility = Visibility.Collapsed; return; }

        var full = mon.Monitor;
        var work = mon.Work;
        bool bottom = work.Bottom < full.Bottom;
        bool top = work.Top > full.Top;
        if (!bottom && !top) { Visibility = Visibility.Collapsed; return; } // vertical or no taskbar

        double s = DipScale();
        double bandTop = (bottom ? work.Bottom : full.Top) * s;
        double bandHeight = (bottom ? full.Bottom - work.Bottom : work.Top - full.Top) * s;

        // Right edge tracks the tray dynamically so the pill always sits just before it.
        double rightBound;
        if (mon.Primary)
        {
            var tray = Native.GetTrayRect();
            rightBound = tray is { } t ? t.Left * s - TrayMarginDip : full.Right * s - 220;
        }
        else
        {
            rightBound = full.Right * s - 120;
        }

        Visibility = Visibility.Visible;
        Root.Height = Math.Max(0, bandHeight - VerticalInset);

        double left = rightBound - Width;
        var geometry = (Math.Round(left), Math.Round(bandTop), Math.Round(bandHeight), Math.Round(rightBound));
        if (geometry == _lastGeometry) return;
        _lastGeometry = geometry;

        Top = bandTop;
        Left = left;
        _toasts?.Layout(); // notifications follow the widget
    }

    // ----- rendering -----

    private void Render(string label, bool animate, bool dot = false)
    {
        StatusText.Text = label;
        if (animate)
        {
            if (!_animating) { _animating = true; _lastRenderTime = TimeSpan.Zero; }
        }
        else
        {
            _animating = false;
            _frame = 0;
            IconImg.Source = dot ? IconRenderer.Dot(Amber) : RestingIcon();
        }
    }

    private ImageSource RestingIcon() =>
        _settings.Style == AnimStyle.Crab ? IconRenderer.Crab(0) : IconRenderer.Logo(IconColor);

    private void OnRender(object? sender, EventArgs e)
    {
        if (_closed || !_animating) return;

        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastRenderTime == TimeSpan.Zero) { _lastRenderTime = now; return; }
        double dt = (now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;

        _frameAccumulator += dt * Fps;
        if (_frameAccumulator >= 1)
        {
            _frame = (_frame + (int)_frameAccumulator) % FrameCount;
            _frameAccumulator %= 1;
            DrawFrame();
        }
        UpdateTimer();
    }

    private void DrawFrame()
    {
        IconImg.Source = _settings.Style switch
        {
            AnimStyle.Crab => IconRenderer.Crab(_frame),
            _ => IconRenderer.Spark(_frame % IconRenderer.SparkFrameCount, IconColor),
        };
    }

    private void UpdateTimer()
    {
        TimerText.Text = _settings.ShowTimer && _startedAt > 0 ? SessionText.Duration(StateStore.UnixNow() - _startedAt) : "";
    }

    // ----- session dots -----

    private enum DotKind { Idle, Working, Permission }

    private static DotKind KindOf(string state) => state switch
    {
        "thinking" or "tool" => DotKind.Working,
        "permission" => DotKind.Permission,
        _ => DotKind.Idle,
    };

    /// <summary>One dot per session (2+ sessions), the focused one ringed, plus a tooltip listing them.</summary>
    private void UpdateDots(IReadOnlyList<ClaudeState> sessions, ClaudeState focus)
    {
        if (sessions.Count < 2)
        {
            if (_dotsSignature == 0) return;
            _dotsSignature = 0;
            SessionDots.Children.Clear();
            SessionDots.Visibility = Visibility.Collapsed;
            _panel?.Close();
            SetPulse(false);
            return;
        }

        // With more sessions than dots, show the ones that most need attention, in their usual order.
        var shown = sessions.Count <= MaxDots
            ? sessions
            : sessions.Select((s, i) => (s, i))
                      .OrderByDescending(x => SessionFocus.Rank(x.s.State)).ThenBy(x => x.i)
                      .Take(MaxDots - 1).OrderBy(x => x.i).Select(x => x.s).ToList();
        int hidden = sessions.Count - shown.Count;

        var hash = new HashCode();
        foreach (var s in shown) { hash.Add(s.SessionId); hash.Add(KindOf(s.State)); }
        hash.Add(focus.SessionId);
        hash.Add(hidden);
        int signature = hash.ToHashCode() | 1; // never 0, which means "no dots"
        if (signature == _dotsSignature) return;
        _dotsSignature = signature;

        SessionDots.Children.Clear();
        foreach (var s in shown)
            SessionDots.Children.Add(MakeDot(s.SessionId, KindOf(s.State), s.SessionId == focus.SessionId));
        if (hidden > 0)
            SessionDots.Children.Add(new TextBlock
            {
                Text = $"+{hidden}", FontSize = 10, Foreground = UsageFill,
                VerticalAlignment = VerticalAlignment.Center,
            });
        SessionDots.Visibility = Visibility.Visible;
        HoverDot(_hoveredDotId);
        // Any working session, shown or behind "+N": the panel's dots share this brush.
        SetPulse(sessions.Any(s => KindOf(s.State) == DotKind.Working));
    }

    private Brush DotBrush(string state) => KindOf(state) switch
    {
        DotKind.Working => _dotWorking,
        DotKind.Permission => DotPermission,
        _ => DotIdle,
    };

    private SessionsPanel Panel()
    {
        if (_panel is not null) return _panel;
        _panel = new SessionsPanel(Root, DotBrush);
        _panel.Opened += () => { _openPopups++; _panel.Refresh(_sessionsNow, _focusId); };
        _panel.Closed += () => { PopupClosed(); HoverDot(null); };
        _panel.RowClicked += OpenSession;
        _panel.HighlightChanged += id => RingDot(id);
        return _panel;
    }

    // The dot under the mouse: ring it on the pill and highlight its row in the panel.
    private void HoverDot(string? sessionId)
    {
        _hoveredDotId = sessionId;
        RingDot(sessionId);
        _panel?.SetHighlight(sessionId);
    }

    // Enlarge and ring one dot (null: none); the focused session's dot keeps its ring.
    private void RingDot(string? sessionId)
    {
        foreach (var child in SessionDots.Children)
        {
            if (child is not Border { Tag: string id, Child: Ellipse dot }) continue;
            bool hovered = id == sessionId;
            dot.Width = dot.Height = hovered ? DotSize + 2 : DotSize;
            dot.Stroke = hovered || id == _focusId ? DotFocusRing : null;
            dot.StrokeThickness = dot.Stroke is null ? 0 : 1;
        }
    }

    // The dot sits in a transparent, full-height hit box tagged with its session, so a click
    // anywhere around it counts.
    private Border MakeDot(string sessionId, DotKind kind, bool focused) => new()
    {
        Tag = sessionId,
        Width = DotSize + 4, Height = 18,
        Background = Brushes.Transparent,
        Child = new Ellipse
        {
            Width = DotSize, Height = DotSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = kind switch { DotKind.Working => _dotWorking, DotKind.Permission => DotPermission, _ => DotIdle },
            Stroke = focused ? DotFocusRing : null,
            StrokeThickness = focused ? 1 : 0,
            IsHitTestVisible = false,
        },
    };

    // A single animation on the shared brush keeps every working dot pulsing in step; it runs
    // only while a working dot is shown, so an idle widget doesn't keep the renderer busy.
    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        _dotWorking.BeginAnimation(Brush.OpacityProperty, on
            ? new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    // ----- usage limits -----

    private void UpdateUsage()
    {
        var usage = _settings.ShowUsageBars ? _usage.Poll() : null;
        if (usage is { HasData: false }) usage = null;
        if (usage == _shownUsage) return;
        _shownUsage = usage;
        if (_isController) LogShownUsage(usage); // one window logs, not one per monitor

        if (usage is null)
        {
            UsageBars.Visibility = Visibility.Collapsed;
            return;
        }

        UsageBars.Visibility = Visibility.Visible;
        SetBar(FiveHourFill, usage.FiveHour);
        SetBar(SevenDayFill, usage.SevenDay);
        _usageTip.Text =
            $"5-hour limit: {Describe(usage.FiveHour, withDay: false)}\n" +
            $"Weekly limit: {Describe(usage.SevenDay, withDay: true)}";
    }

    private void LogShownUsage(UsageLimits? usage)
    {
        if (usage is null)
        {
            UsageLog.Write(_settings.ShowUsageBars
                ? "widget     bars hidden: no usage data (usage.json missing or empty)"
                : "widget     bars hidden: turned off in the menu");
            return;
        }
        var asOf = usage.UpdatedAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)usage.UpdatedAt).LocalDateTime.ToString("HH:mm:ss")
            : "?";
        var source = usage.Source.Length > 0 ? usage.Source : "unknown";
        UsageLog.Write($"widget     showing 5h {Describe(usage.FiveHour, withDay: false)}, " +
                       $"7d {Describe(usage.SevenDay, withDay: true)}  (from {source}, as of {asOf})");
    }

    private static void SetBar(Border fill, UsageWindow? window)
    {
        double pct = window?.Percent ?? 0;
        fill.Height = pct > 0 ? Math.Max(2, Math.Round(UsageBarHeight * pct / 100)) : 0; // a sliver stays visible
        fill.Background = pct >= UsageWarnPercent ? UsageWarnFill : UsageFill;
    }

    private static string Describe(UsageWindow? window, bool withDay)
    {
        if (window is null) return "no data";
        var text = $"{Math.Round(window.Percent)}% used";
        if (window.ResetsAt > 0)
        {
            var resets = DateTimeOffset.FromUnixTimeSeconds((long)window.ResetsAt).LocalDateTime;
            // Day and short time formatted separately: inside a custom format "t" is the AM/PM designator.
            text += withDay ? $" · resets {resets:ddd} {resets:t}" : $" · resets {resets:t}";
        }
        return text;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ----- thinking verbs -----

    private string ThinkingLabel()
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_verbStartedAt == 0 || now - _verbStartedAt >= VerbRotateSeconds)
        {
            _verbStartedAt = now;
            _verb = PickVerb();
        }
        return _verb + "…";
    }

    private string PickVerb()
    {
        var verbs = ThinkingVerbs.All;
        if (verbs.Length == 0) return "Thinking";
        int next = _verbIndex;
        for (int i = 0; i < 3 && next == _verbIndex; i++)
            next = (_verbIndex + 7 + (int)(DateTimeOffset.UtcNow.Ticks % (verbs.Length - 1)) + 1) % verbs.Length;
        _verbIndex = next;
        return verbs[next];
    }

    // ----- chime and notifications -----

    // Only the controller window runs this, so several monitors don't chime or notify at once.
    // A session seen for the first time produces no events.
    private void HandleTransitions(IReadOnlyList<ClaudeState> sessions)
    {
        var events = _transitions.Update(sessions);
        if (events.Count == 0) return;

        bool finished = false;
        foreach (var e in events)
        {
            if (e.Kind == SessionEventKind.Finished) finished = true;
            if (NotificationPolicy.Dismisses(e.Kind)) _toasts?.Dismiss(e.Session.SessionId);
            else if (ShouldNotify(e)) Toasts().Show(e, SessionNames.Build(sessions).GetValueOrDefault(e.Session.SessionId, e.Session.Project));
        }
        if (finished && _settings.PlayCompletionSound) PlayChime();
    }

    // Settings first (free); the system queries only for events that would otherwise notify.
    private bool ShouldNotify(SessionEvent e) =>
        NotificationPolicy.ShouldShow(e, _settings, userBusy: false, lookingAtSession: false)
        && NotificationPolicy.ShouldShow(e, _settings, Native.UserBusy(), SessionWindowFinder.IsForeground(e.Session));

    private ToastManager Toasts()
    {
        if (_toasts is not null) return _toasts;
        // The pill is right-aligned in the (wider, transparent) window, so its left edge is
        // the window's right edge minus the pill's width.
        _toasts = new ToastManager(() => (Left + Width - Root.ActualWidth, Root.ActualWidth, Top + Root.Margin.Top), Brand, Amber);
        _toasts.OpenRequested += id =>
        {
            var session = _sessionsNow.FirstOrDefault(s => s.SessionId == id);
            if (session is not null) OpenSession(session);
        };
        return _toasts;
    }

    private void PlayChime()
    {
        try
        {
            if (_chime is null)
            {
                var path = ExtractChime();
                if (path is null) return;
                _chime = new MediaPlayer { Volume = 0.7 };
                _chime.Open(new Uri(path));
            }
            _chime.Position = TimeSpan.Zero;
            _chime.Play();
        }
        catch { }
    }

    // The chime is embedded in the assembly; MediaPlayer needs a file URI, so extract it once
    // to a temp file on first use.
    private static string? ExtractChime()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("completion.mp3", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeStatusBar.completion.mp3");
            if (!System.IO.File.Exists(path))
            {
                using var src = asm.GetManifestResourceStream(name);
                if (src is null) return null;
                using var dst = System.IO.File.Create(path);
                src.CopyTo(dst);
            }
            return path;
        }
        catch { return null; }
    }

    // ----- lifecycle -----

    private void CheckLifecycle()
    {
        if (!_isController) return;
        if ((DateTime.UtcNow - _launchedAt).TotalSeconds < 8) return;

        if (_store.SessionCount() > 0) { _sawSession = true; _idleSince = null; return; }
        if (!_sawSession) return;

        _idleSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _idleSince.Value).TotalSeconds >= 30)
            Application.Current.Shutdown();
    }

    // ----- context menu -----

    // Left-click opens a session's window: the clicked dot's, otherwise the one the label shows.
    private void OnOpenClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var id = (e.OriginalSource as FrameworkElement)?.Tag as string ?? _focusId;
        var session = _sessionsNow.FirstOrDefault(s => s.SessionId == id);
        if (session is not null) OpenSession(session);
    }

    private void OpenSession(ClaudeState session)
    {
        var hwnd = SessionWindowFinder.Find(session);
        if (hwnd == IntPtr.Zero) ShowNotice("Can't find its window");
        else SessionWindowFinder.Activate(hwnd);
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        _noticeUntil = DateTime.UtcNow.AddSeconds(2.5);
        StatusText.Text = text;
    }

    private void OnMenuClick(object sender, MouseButtonEventArgs e)
    {
        if (Menu == null) return;
        Activate();
        Menu.PlacementTarget = Root;
        Menu.Placement = PlacementMode.Top;
        Menu.IsOpen = true;
        e.Handled = true;
    }

    private void SyncMenuChecks()
    {
        MiTimer.IsChecked = _settings.ShowTimer;
        MiUsage.IsChecked = _settings.ShowUsageBars;
#if !OFFLINE
        SyncOnlineUsageMenu();
#endif
        MiSound.IsChecked = _settings.PlayCompletionSound;
        MiNotify.IsChecked = _settings.ShowNotifications;
        MiNotifyPermission.IsChecked = _settings.NotifyPermission;
        MiNotifyFinished.IsChecked = _settings.NotifyFinished;
        MiNotifyPermission.IsEnabled = MiNotifyFinished.IsEnabled = _settings.ShowNotifications;
        MiStyleSpark.IsChecked = _settings.Style == AnimStyle.Spark;
        MiStyleCode.IsChecked = _settings.Style == AnimStyle.Code;
        MiStyleCrab.IsChecked = _settings.Style == AnimStyle.Crab;
        MiVersion.Header = $"Version {AppVersion} · {BuildInfo.Variant}";
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0";

    private void OnToggleTimer(object sender, RoutedEventArgs e)
    {
        _settings.ShowTimer = !_settings.ShowTimer;
        _settings.Save();
        OverlayManager.Instance?.SyncAll();
    }

    private void OnToggleUsage(object sender, RoutedEventArgs e)
    {
        _settings.ShowUsageBars = !_settings.ShowUsageBars;
        _settings.Save();
        OverlayManager.Instance?.SyncAll();
    }

#if !OFFLINE
    // ----- online build only: the usage API menu -----

    private readonly MenuItem MiUsageOnline = new() { Header = "Fetch usage online", IsCheckable = true };
    private readonly MenuItem MiUsageRefresh = new() { Header = "Refresh usage every" };

    private void AddOnlineUsageMenu()
    {
        MiUsageOnline.Click += OnToggleUsageOnline;
        foreach (int minutes in new[] { 2, 5, 10, 15 })
        {
            var item = new MenuItem { Header = $"{minutes} minutes", Tag = minutes.ToString(), IsCheckable = true };
            item.Click += OnUsageRefresh;
            MiUsageRefresh.Items.Add(item);
        }
        int at = Menu.Items.IndexOf(MiUsage) + 1;
        Menu.Items.Insert(at, MiUsageOnline);
        Menu.Items.Insert(at + 1, MiUsageRefresh);
    }

    private void SyncOnlineUsageMenu()
    {
        MiUsageOnline.IsChecked = _settings.FetchUsageOnline;
        MiUsageRefresh.IsEnabled = _settings.FetchUsageOnline;
        foreach (var item in MiUsageRefresh.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string t && t == _settings.UsageRefreshMinutes.ToString();
    }

    private void OnToggleUsageOnline(object sender, RoutedEventArgs e)
    {
        _settings.FetchUsageOnline = !_settings.FetchUsageOnline;
        _settings.Save();
        UsageUpdater.Instance?.Refresh(); // switch sources now rather than at the next poll
        OverlayManager.Instance?.SyncAll();
    }

    private void OnUsageRefresh(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out int minutes)) return;
        _settings.UsageRefreshMinutes = minutes;
        _settings.Save();
        UsageUpdater.Instance?.Refresh();
        OverlayManager.Instance?.SyncAll();
    }
#endif

    private void OnToggleNotify(object sender, RoutedEventArgs e) =>
        ChangeNotifications(() => _settings.ShowNotifications = !_settings.ShowNotifications);

    private void OnToggleNotifyPermission(object sender, RoutedEventArgs e) =>
        ChangeNotifications(() => _settings.NotifyPermission = !_settings.NotifyPermission);

    private void OnToggleNotifyFinished(object sender, RoutedEventArgs e) =>
        ChangeNotifications(() => _settings.NotifyFinished = !_settings.NotifyFinished);

    private void ChangeNotifications(Action change)
    {
        change();
        _settings.Save();
        if (!_settings.ShowNotifications) _toasts?.DismissAll();
        OverlayManager.Instance?.SyncAll();
    }

    private void OnToggleSound(object sender, RoutedEventArgs e)
    {
        _settings.PlayCompletionSound = !_settings.PlayCompletionSound;
        _settings.Save();
        OverlayManager.Instance?.SyncAll();
    }

    private void OnStyleSpark(object sender, RoutedEventArgs e) => SetStyle(AnimStyle.Spark);
    private void OnStyleCode(object sender, RoutedEventArgs e) => SetStyle(AnimStyle.Code);
    private void OnStyleCrab(object sender, RoutedEventArgs e) => SetStyle(AnimStyle.Crab);

    private void SetStyle(AnimStyle style)
    {
        _settings.Style = style;
        _settings.Save();
        OverlayManager.Instance?.SyncAll();
    }

    /// <summary>Re-read shared settings into this window after another window changed them.</summary>
    public void ApplySettings()
    {
        if (_closed) return;
        _animating = false;
        _frame = 0;
        _lastRenderTime = TimeSpan.Zero;
        SyncMenuChecks();
        Tick();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnMonitorsOpening(object sender, RoutedEventArgs e)
    {
        MiMonitors.Items.Clear();
        var monitors = Native.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            bool shown = _settings.Monitors.Count == 0 ? m.Primary : _settings.Monitors.Contains(m.Device);
            var item = new MenuItem
            {
                Header = $"Display {i + 1}{(m.Primary ? " (primary)" : "")}  {m.Monitor.Width}×{m.Monitor.Height}",
                IsCheckable = true,
                IsChecked = shown,
                Tag = m.Device,
            };
            item.Click += OnToggleMonitor;
            MiMonitors.Items.Add(item);
        }
    }

    private void OnToggleMonitor(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string device) return;
        var monitors = Native.GetMonitors();

        var set = new HashSet<string>(_settings.Monitors.Count == 0
            ? monitors.Where(m => m.Primary).Select(m => m.Device)
            : _settings.Monitors);

        if (item.IsChecked) set.Add(device); else set.Remove(device);
        if (set.Count == 0)
        {
            var primary = monitors.FirstOrDefault(m => m.Primary);
            if (primary != null) set.Add(primary.Device);
        }

        _settings.Monitors = set.ToList();
        _settings.Save();
        OverlayManager.Instance?.Rebuild();
    }
}
