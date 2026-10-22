using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeStatusBar;

/// <summary>
/// The panel that lists every session above the pill when the mouse rests on the session dots.
/// One row per session (status dot, name, status, timer); the row of the dot under the mouse is
/// highlighted and hovering a row highlights its dot, so it is clear which session a click opens.
///
/// Kept cheap: created on first hover and reused; rows are pooled and updated in place; content
/// is refreshed only while the panel is open.
/// </summary>
internal sealed class SessionsPanel
{
    private const double Width = 300;
    private const double RowHeight = 24;
    private const int MaxVisibleRows = 12;
    private const double GapAbovePill = 6;

    private static readonly Brush PanelBackground = Frozen(Color.FromArgb(0xF0, 0x26, 0x26, 0x26));
    private static readonly Brush PanelBorder = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush NameText = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush StatusText = Frozen(Color.FromRgb(0xC0, 0xC0, 0xC0));
    private static readonly Brush RowHighlight = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily TimerFont = new("Consolas");

    private readonly Popup _popup;
    private readonly StackPanel _rowsHost = new();
    private readonly List<Row> _rows = new();
    private readonly Func<string, Brush> _dotBrush;
    private readonly DispatcherTimer _hoverDelay = new();
    private bool _pointerInside;
    private string? _highlightId;

    /// <summary>A row was clicked: open that session.</summary>
    public event Action<ClaudeState>? RowClicked;

    /// <summary>The highlighted session changed (from hovering a row), so the pill can ring its dot.</summary>
    public event Action<string?>? HighlightChanged;

    public event Action? Opened;
    public event Action? Closed;

    public bool IsOpen => _popup.IsOpen;

    /// <param name="target">The pill; the panel sits above it, right-aligned.</param>
    /// <param name="dotBrush">Fill for a session's dot, given its state (shared with the pill's dots).</param>
    public SessionsPanel(FrameworkElement target, Func<string, Brush> dotBrush)
    {
        _dotBrush = dotBrush;
        var scroll = new ScrollViewer
        {
            Content = _rowsHost,
            MaxHeight = RowHeight * MaxVisibleRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var frame = new Border
        {
            Width = Width,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(7),
            Background = PanelBackground,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            Child = scroll,
        };
        frame.MouseEnter += (_, _) => PointerEntered();
        frame.MouseLeave += (_, _) => PointerLeft();

        _popup = new Popup
        {
            Child = frame,
            PlacementTarget = target,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = PlaceAboveRightAligned,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
        };
        _popup.Opened += (_, _) => Opened?.Invoke();
        _popup.Closed += (_, _) => { SetHighlight(null); Closed?.Invoke(); };

        _hoverDelay.Tick += (_, _) =>
        {
            _hoverDelay.Stop();
            if (_pointerInside && !_popup.IsOpen) _popup.IsOpen = true;
            else if (!_pointerInside && _popup.IsOpen) _popup.IsOpen = false;
        };
    }

    private static CustomPopupPlacement[] PlaceAboveRightAligned(Size popup, Size target, Point offset) =>
        [new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - GapAbovePill), PopupPrimaryAxis.None)];

    // ----- hover: open after a short rest, close a little after the mouse leaves both the dots and the panel -----

    public void PointerEntered()
    {
        _pointerInside = true;
        Restart(_popup.IsOpen ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250));
    }

    public void PointerLeft()
    {
        _pointerInside = false;
        Restart(TimeSpan.FromMilliseconds(300));
    }

    public void Close()
    {
        _pointerInside = false;
        _hoverDelay.Stop();
        _popup.IsOpen = false;
    }

    private void Restart(TimeSpan delay)
    {
        _hoverDelay.Stop();
        if (delay == TimeSpan.Zero) return;
        _hoverDelay.Interval = delay;
        _hoverDelay.Start();
    }

    // ----- content -----

    /// <summary>Show these sessions; call each poll while open (cheap: rows are updated in place).</summary>
    public void Refresh(IReadOnlyList<ClaudeState> sessions, string? focusId)
    {
        var names = SessionNames.Build(sessions);
        double now = StateStore.UnixNow();
        while (_rows.Count < sessions.Count)
        {
            var row = new Row(this);
            _rows.Add(row);
            _rowsHost.Children.Add(row.Root);
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i < sessions.Count) _rows[i].Show(sessions[i], names[sessions[i].SessionId], now, focusId);
            else _rows[i].Hide();
        }
        ApplyHighlight();
    }

    /// <summary>Highlight a session's row (from the pill: the dot under the mouse); null clears it.</summary>
    public void SetHighlight(string? sessionId)
    {
        if (_highlightId == sessionId) return;
        _highlightId = sessionId;
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        foreach (var row in _rows)
            row.Root.Background = row.Session is not null && row.Session.SessionId == _highlightId ? RowHighlight : Brushes.Transparent;
    }

    private void RowHovered(Row row, bool inside)
    {
        SetHighlight(inside ? row.Session?.SessionId : null);
        HighlightChanged?.Invoke(_highlightId);
    }

    private void RowClick(Row row)
    {
        if (row.Session is { } session) RowClicked?.Invoke(session);
        Close();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>One pooled row: dot, name, status, timer.</summary>
    private sealed class Row
    {
        public readonly Border Root;
        public ClaudeState? Session;
        private readonly SessionsPanel _panel;
        private readonly Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = Text(NameText, 120);
        private readonly TextBlock _status = Text(StatusText, double.PositiveInfinity);
        private readonly TextBlock _timer = Text(StatusText, double.PositiveInfinity);

        public Row(SessionsPanel panel)
        {
            _panel = panel;
            _timer.FontFamily = TimerFont;
            _timer.Margin = new Thickness(8, 0, 0, 0);
            _status.Margin = new Thickness(8, 0, 0, 0);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_name, 1);
            Grid.SetColumn(_status, 2);
            Grid.SetColumn(_timer, 3);
            grid.Children.Add(_dot);
            grid.Children.Add(_name);
            grid.Children.Add(_status);
            grid.Children.Add(_timer);

            Root = new Border
            {
                Height = RowHeight,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = grid,
            };
            Root.MouseEnter += (_, _) => _panel.RowHovered(this, inside: true);
            Root.MouseLeave += (_, _) => _panel.RowHovered(this, inside: false);
            Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; _panel.RowClick(this); };
        }

        public void Show(ClaudeState session, string name, double now, string? focusId)
        {
            Session = session;
            Root.Visibility = Visibility.Visible;
            _dot.Fill = _panel._dotBrush(session.State);
            _dot.Stroke = session.SessionId == focusId ? Brushes.White : null;
            _dot.StrokeThickness = session.SessionId == focusId ? 1 : 0;
            SetText(_name, name);
            SetText(_status, SessionText.Status(session));
            SetText(_timer, session.State is "thinking" or "tool" or "background" && session.StartedAt > 0 ? SessionText.Duration(now - session.StartedAt) : "");
        }

        public void Hide()
        {
            Session = null;
            Root.Visibility = Visibility.Collapsed;
        }

        // Assigning an equal string still invalidates layout, so skip it.
        private static void SetText(TextBlock block, string text)
        {
            if (block.Text != text) block.Text = text;
        }

        private static TextBlock Text(Brush foreground, double maxWidth) => new()
        {
            FontSize = 12,
            Foreground = foreground,
            MaxWidth = maxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
