using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeStatusBar;

/// <summary>
/// One notification, styled like the pill: icon, project name, what happened, and a close mark
/// on hover. It never takes focus (WS_EX_NOACTIVATE) and stays out of Alt-Tab. A "finished"
/// notification closes itself after a few seconds (paused while the mouse is on it); a
/// permission notification stays until the session moves on, it is clicked, or it is closed.
/// </summary>
internal sealed class ToastWindow : Window
{
    public const double ToastHeight = 52;
    private static readonly TimeSpan AutoCloseAfter = TimeSpan.FromSeconds(6);

    private static readonly Brush ToastBackground = Frozen(Color.FromArgb(0xF0, 0x26, 0x26, 0x26));
    private static readonly Brush ToastBorder = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush NameText = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush DetailText = Frozen(Color.FromRgb(0xC0, 0xC0, 0xC0));

    private readonly TextBlock _closeMark;
    private DispatcherTimer? _autoClose;

    public string SessionId { get; }

    /// <summary>The notification body was clicked: open the session.</summary>
    public event Action<ToastWindow>? Clicked;

    /// <summary>The notification closed itself or was closed with the × (not by a click on the body).</summary>
    public event Action<ToastWindow>? Dismissed;

    public ToastWindow(SessionEvent e, string name, Color brand, Color amber)
    {
        SessionId = e.Session.SessionId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Height = ToastHeight; // the width follows the pill (ToastManager.Layout)

        bool permission = e.Kind == SessionEventKind.PermissionRequested;
        var detail = permission
            ? string.IsNullOrEmpty(e.Session.Detail) ? "Needs your permission" : $"Needs permission · {SessionText.PermissionSubject(e.Session.Detail)}"
            : $"Finished · {SessionText.Duration(e.Session.LastTurnSeconds)}";

        var icon = new Image
        {
            Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Source = permission ? IconRenderer.Dot(amber) : IconRenderer.Logo(brand), // rendered once, shared
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Line(name, 12, NameText));
        text.Children.Add(Line(detail, 11, DetailText));

        _closeMark = new TextBlock
        {
            Text = "×", FontSize = 15, Foreground = DetailText, Padding = new Thickness(8, 0, 2, 2),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, Cursor = Cursors.Arrow,
        };
        _closeMark.MouseLeftButtonUp += (_, args) => { args.Handled = true; Dismiss(); Dismissed?.Invoke(this); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_closeMark, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(_closeMark);

        var body = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = ToastBackground,
            BorderBrush = ToastBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        body.MouseLeftButtonUp += (_, args) => { args.Handled = true; Clicked?.Invoke(this); };
        AutomationProperties.SetName(body, $"{name}: {detail}");
        Content = body;

        MouseEnter += (_, _) => { _closeMark.Visibility = Visibility.Visible; _autoClose?.Stop(); };
        MouseLeave += (_, _) => { _closeMark.Visibility = Visibility.Hidden; _autoClose?.Start(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        };

        if (!permission)
        {
            _autoClose = new DispatcherTimer { Interval = AutoCloseAfter };
            _autoClose.Tick += OnAutoClose;
        }
    }

    public void ShowAt(double left, double top, double width)
    {
        Left = left;
        Top = top;
        Width = width;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop });
        _autoClose?.Start();
    }

    /// <summary>Close for good, releasing the window and its timer.</summary>
    public void Dismiss()
    {
        if (_autoClose is not null)
        {
            _autoClose.Stop();
            _autoClose.Tick -= OnAutoClose;
            _autoClose = null;
        }
        Close();
    }

    private void OnAutoClose(object? sender, EventArgs e)
    {
        Dismiss();
        Dismissed?.Invoke(this);
    }

    private static TextBlock Line(string text, double size, Brush foreground) => new()
    {
        Text = text, FontSize = size, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Shows notifications stacked above the pill (newest nearest it, at most
/// <see cref="ToastLayout.MaxToasts"/>), one per session, as wide as the pill and aligned with
/// it; they follow it when it moves or changes width. Windows exist only while shown.
/// </summary>
internal sealed class ToastManager
{
    private readonly List<ToastWindow> _toasts = new(); // newest first
    private readonly Func<(double Left, double Width, double Top)> _pill;
    private readonly Color _brand, _amber;

    /// <summary>A notification was clicked: open this session.</summary>
    public event Action<string>? OpenRequested;

    /// <param name="pill">The pill's left edge, width and top, in DIPs.</param>
    public ToastManager(Func<(double Left, double Width, double Top)> pill, Color brand, Color amber)
    {
        _pill = pill;
        _brand = brand;
        _amber = amber;
    }

    public int Count => _toasts.Count;

    public void Show(SessionEvent e, string name)
    {
        Dismiss(e.Session.SessionId); // one per session: the newer event replaces it
        var toast = new ToastWindow(e, name, _brand, _amber);
        toast.Clicked += OnClicked;
        toast.Dismissed += Forget;
        _toasts.Insert(0, toast);
        while (_toasts.Count > ToastLayout.MaxToasts) Close(_toasts[^1]);
        Layout();
    }

    public void Dismiss(string sessionId)
    {
        var toast = _toasts.FirstOrDefault(t => t.SessionId == sessionId);
        if (toast is null) return;
        Close(toast);
        Layout();
    }

    public void DismissAll()
    {
        foreach (var toast in _toasts.ToList()) Close(toast);
    }

    /// <summary>Place every notification; call when one is added or removed, or the pill moves or resizes.</summary>
    public void Layout()
    {
        if (_toasts.Count == 0) return;
        var (left, width, top) = _pill();
        var bounds = ToastLayout.Place(left, width, top, _toasts.Count, ToastWindow.ToastHeight);
        for (int i = 0; i < _toasts.Count; i++)
        {
            var toast = _toasts[i];
            var b = bounds[i];
            if (!toast.IsVisible) { toast.ShowAt(b.Left, b.Top, b.Width); continue; }
            if (toast.Left != b.Left) toast.Left = b.Left;  // skip no-op moves: each one is a window move
            if (toast.Top != b.Top) toast.Top = b.Top;
            if (toast.Width != b.Width) toast.Width = b.Width;
        }
    }

    private void OnClicked(ToastWindow toast)
    {
        var id = toast.SessionId;
        Close(toast);
        Layout();
        OpenRequested?.Invoke(id);
    }

    // The toast closed itself (timer or ×): drop it and close the gap.
    private void Forget(ToastWindow toast)
    {
        Unhook(toast);
        _toasts.Remove(toast);
        Layout();
    }

    private void Close(ToastWindow toast)
    {
        Unhook(toast);
        _toasts.Remove(toast);
        toast.Dismiss();
    }

    private void Unhook(ToastWindow toast)
    {
        toast.Clicked -= OnClicked;
        toast.Dismissed -= Forget;
    }
}
