<#
.SYNOPSIS
  Checks the running widget's popups and clicks with real mouse input.

.DESCRIPTION
  usage, panel, menu: hovers a part of the widget (the usage bars, the session dots; right-clicks
  it for the menu), waits several 400 ms poll ticks, then inspects the window z-order: the popup
  must sit above the widget and must not overlap the pill. For the panel, -ExpectRows also checks
  its height. Finally checks that the widget is back above the taskbar once the popup has closed.

  notify: puts Notepad in front and watches for -WaitSeconds while something (the session
  simulator) makes sessions ask for permission or finish: at least one notification must appear,
  never more than 3 at once, and the foreground window must never change (they never take focus).

  open: puts another window (Notepad) in front, left-clicks the widget (-At label or dot), and
  checks that the foreground window becomes the session's window (title matching -ExpectTitle).

  opentab: the same click, for a session in an editor's Claude Code panel, with another tab of
  that window active beforehand. Passes when the foreground window's title shows the session's
  tab (-ExpectTab) within -WaitSeconds, and reports how long that took. VS Code-based editors put
  the active tab's name in the window title (the default window.title); their tabs aren't
  visible to UI Automation.

  Saves screenshots on request and restores the cursor. Exits 0 on PASS, 1 on FAIL.

.EXAMPLE
  .\tools\check-ui.ps1 -Target usage
  .\tools\check-ui.ps1 -Target panel -ExpectRows 4 -Screenshot panel.png
  .\tools\check-ui.ps1 -Target menu
  .\tools\check-ui.ps1 -Target open -At label -ExpectTitle "claude-status-bar"
  .\tools\check-ui.ps1 -Target opentab -At dot -ExpectTab "say hi" -WaitSeconds 8
  .\tools\check-ui.ps1 -Target notify -WaitSeconds 60   # with simulate-sessions.js running
#>
param(
    [ValidateSet("usage", "panel", "menu", "open", "opentab", "notify")] [string]$Target = "usage",
    [int]$ExpectRows = 0,
    [ValidateSet("label", "dot")] [string]$At = "label",
    [string]$ExpectTitle = "",
    [string]$ExpectTab = "",
    [double]$WaitSeconds = 3,
    [string]$Screenshot = "",
    # Another screenshot 2.5 s later with the popup still open, to see its timers advance.
    [string]$SecondScreenshot = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class ZOrder {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public class Win { public IntPtr Hwnd; public uint Pid; public RECT Rect; public string Class; public int Z; }
  // Visible top-level windows, topmost first (EnumWindows walks the z-order top to bottom).
  public static List<Win> All() {
    var list = new List<Win>(); int z = 0;
    EnumWindows((h, l) => {
      if (IsWindowVisible(h)) {
        uint pid; GetWindowThreadProcessId(h, out pid); RECT r; GetWindowRect(h, out r);
        var sb = new StringBuilder(256); GetClassName(h, sb, 256);
        list.Add(new Win { Hwnd = h, Pid = pid, Rect = r, Class = sb.ToString(), Z = z });
      }
      z++; return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$proc = Get-Process ClaudeStatusBar -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "FAIL: the widget isn't running"; exit 1 }

function Get-WidgetWindows {
    $mine = [ZOrder]::All() | Where-Object { $_.Pid -eq $proc.Id }
    # The widget is the widest window (its transparent strip included); popups are the others.
    $widget = $mine | Sort-Object { $_.Rect.R - $_.Rect.L } -Descending | Select-Object -First 1
    $popups = $mine | Where-Object { $_.Hwnd -ne $widget.Hwnd }
    return $widget, $popups
}

$widget, $null = Get-WidgetWindows
$scale = [ZOrder]::GetDpiForWindow($widget.Hwnd) / 96.0
$r = $widget.Rect
$pillTop = $r.T + [int](3 * $scale)                     # Root has a 3px top margin
$midY = [int](($r.T + $r.B) / 2)
$right = $r.R - [int](13 * $scale)                      # 12px padding + 1px border

# Element positions follow the XAML layout from the pill's right edge: the usage bars (6px margin
# + 4+3+4), then the session dots (the last dot's centre is 4px margin + 3px from their edge).
$point = switch ($Target) {
    "usage" { [System.Drawing.Point]::new($right - [int](6 * $scale), $midY) }
    "panel" { [System.Drawing.Point]::new($right - [int]((17 + 7) * $scale), $midY) }
    "menu"  { [System.Drawing.Point]::new($right - [int](120 * $scale), $midY) }
    { $_ -in "open", "opentab" } {
        if ($At -eq "dot") { [System.Drawing.Point]::new($right - [int]((17 + 7) * $scale), $midY) }
        else { [System.Drawing.Point]::new($right - [int](120 * $scale), $midY) }
    }
}

if ($Target -eq "notify") {
    $other = Start-Process notepad -PassThru
    $seen = 0; $most = 0; $stolen = @(); $misaligned = 0
    $pillRect = $null
    try {
        Start-Sleep -Milliseconds 1200
        $front = [ZOrder]::GetForegroundWindow()
        # The pill is right-aligned in the widget window, so its right edge is the window's.
        $pillRect = [pscustomobject]@{ R = $widget.Rect.R }
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $null, $popups = Get-WidgetWindows
            # Notifications: 52 DIPs tall and as wide as the pill (220-320 DIPs) windows of the widget process.
            $toasts = @($popups | Where-Object {
                $w = $_.Rect.R - $_.Rect.L
                [math]::Abs(($_.Rect.B - $_.Rect.T) - 52 * $scale) -le 2 -and $w -ge 220 * $scale -and $w -le 320 * $scale })
            # ...and aligned with the pill: same width and right edge for all of them.
            foreach ($t in $toasts) { if ($null -ne $pillRect -and [math]::Abs($t.Rect.R - $pillRect.R) -gt 2) { $misaligned++ } }
            if ($toasts.Count -gt 0) { $seen++ }
            if ($toasts.Count -gt $most) { $most = $toasts.Count }
            $now = [ZOrder]::GetForegroundWindow()
            if ($now -ne $front) { $stolen += [ZOrder]::Title($now); $front = $now }
        }
    }
    finally { Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue }
    $ok = $seen -gt 0 -and $most -le 3 -and $stolen.Count -eq 0 -and $misaligned -eq 0
    Write-Host "$(if ($ok) { 'PASS' } else { 'FAIL' }) (notify)"
    Write-Host "  polls with a notification: $seen; most at once: $most; not aligned with the pill: $misaligned"
    if ($stolen) { Write-Host "  foreground changed to: $($stolen -join ', ')" }
    if ($ok) { exit 0 } else { exit 1 }
}

if ($Target -eq "opentab") {
    if (-not $ExpectTab) { Write-Host "FAIL: -ExpectTab is required"; exit 1 }
    $original = [System.Windows.Forms.Cursor]::Position
    $other = Start-Process notepad -PassThru
    $after = ""; $elapsed = $null
    try {
        Start-Sleep -Milliseconds 1200
        $before = [ZOrder]::Title([ZOrder]::GetForegroundWindow())
        [System.Windows.Forms.Cursor]::Position = $point
        Start-Sleep -Milliseconds 150
        $clock = [Diagnostics.Stopwatch]::StartNew()
        [ZOrder]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [ZOrder]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
        while ($clock.Elapsed.TotalSeconds -lt $WaitSeconds) {
            $after = [ZOrder]::Title([ZOrder]::GetForegroundWindow())
            if ($after -like "*$ExpectTab*") { $elapsed = $clock.ElapsedMilliseconds; break }
            Start-Sleep -Milliseconds 50
        }
    }
    finally {
        [System.Windows.Forms.Cursor]::Position = $original
        Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue
    }
    $ok = $null -ne $elapsed
    Write-Host "$(if ($ok) { "PASS (opentab, clicked $At, tab shown after $elapsed ms)" } else { "FAIL (opentab, clicked $At, tab not shown within $WaitSeconds s)" })"
    Write-Host "  foreground before: $before"
    Write-Host "  foreground after:  $after"
    if ($ok) { exit 0 } else { exit 1 }
}

if ($Target -eq "open") {
    $original = [System.Windows.Forms.Cursor]::Position
    $other = Start-Process notepad -PassThru
    try {
        Start-Sleep -Milliseconds 1200
        $beforeHandle = [ZOrder]::GetForegroundWindow()
        $before = [ZOrder]::Title($beforeHandle)
        [System.Windows.Forms.Cursor]::Position = $point
        Start-Sleep -Milliseconds 150
        [ZOrder]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [ZOrder]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 1000
        $afterHandle = [ZOrder]::GetForegroundWindow()
        $after = [ZOrder]::Title($afterHandle)
    }
    finally {
        [System.Windows.Forms.Cursor]::Position = $original
        Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue
    }
    $ok = $afterHandle -ne $beforeHandle -and (-not $ExpectTitle -or $after -like "*$ExpectTitle*")
    Write-Host "$(if ($ok) { 'PASS' } else { 'FAIL' }) (open, clicked $At)"
    Write-Host "  foreground before: $before"
    Write-Host "  foreground after:  $after"
    if ($ok) { exit 0 } else { exit 1 }
}

$original = [System.Windows.Forms.Cursor]::Position
$result = "PASS"; $notes = @()
try {
    [System.Windows.Forms.Cursor]::Position = $point
    for ($i = 0; $i -lt 4; $i++) { [ZOrder]::mouse_event(0x0001, 1, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60 }
    if ($Target -eq "menu") {   # right-click opens the menu
        [ZOrder]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); [ZOrder]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
    }
    Start-Sleep -Seconds $WaitSeconds                    # several AssertTopmost ticks happen here

    $widget, $popups = Get-WidgetWindows
    if (-not $popups) { $result = "FAIL"; $notes += "no $Target popup appeared" }
    foreach ($p in $popups) {
        $pr = $p.Rect
        $notes += "popup z=$($p.Z) widget z=$($widget.Z) popup rect=($($pr.L),$($pr.T))-($($pr.R),$($pr.B)) pill top=$pillTop"
        if ($p.Z -gt $widget.Z) { $result = "FAIL"; $notes += "popup is BELOW the widget" }
        if ($Target -ne "menu" -and $pr.B -gt $pillTop -and $pr.R -gt $r.L -and $pr.L -lt $r.R) {
            $result = "FAIL"; $notes += "popup overlaps the pill by $($pr.B - $pillTop)px"
        }
        if ($Target -eq "panel" -and $ExpectRows -gt 0) {
            # Rows are 24 DIPs (up to 12 visible), plus 4+4 padding and a 1px border each side.
            $expected = ([Math]::Min($ExpectRows, 12) * 24 + 10) * $scale
            $height = $pr.B - $pr.T
            $notes += "panel height $height px, expected about $([int]$expected) for $ExpectRows rows"
            if ([math]::Abs($height - $expected) -gt 3) { $result = "FAIL"; $notes += "panel height doesn't match $ExpectRows rows" }
        }
    }

    # The pill plus every popup, with a margin.
    function Save-Shot([string]$path) {
        $rects = @($widget.Rect) + @($popups | ForEach-Object { $_.Rect })
        $left = ($rects | ForEach-Object { $_.L } | Measure-Object -Minimum).Minimum - 20
        $top = ($rects | ForEach-Object { $_.T } | Measure-Object -Minimum).Minimum - 20
        $rightEdge = ($rects | ForEach-Object { $_.R } | Measure-Object -Maximum).Maximum + 20
        $bottom = ($rects | ForEach-Object { $_.B } | Measure-Object -Maximum).Maximum
        $bmp = New-Object System.Drawing.Bitmap ([int]($rightEdge - $left)), ([int]($bottom - $top))
        [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen([int]$left, [int]$top, 0, 0, $bmp.Size)
        $bmp.Save($path)
    }
    if ($Screenshot) { Save-Shot $Screenshot }
    if ($SecondScreenshot) { Start-Sleep -Milliseconds 2500; Save-Shot $SecondScreenshot }
}
finally {
    if ($Target -eq "menu") { [ZOrder]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero); [ZOrder]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero) }
    [System.Windows.Forms.Cursor]::Position = $original
    [ZOrder]::mouse_event(0x0001, 1, 0, 0, [IntPtr]::Zero)
}

# After the popup closes the widget must be on top of the taskbar again.
Start-Sleep -Seconds 1.5
$widget, $popups = Get-WidgetWindows
$tray = [ZOrder]::All() | Where-Object { $_.Class -eq "Shell_TrayWnd" } | Select-Object -First 1
if ($popups) { $result = "FAIL"; $notes += "popup still open after moving away" }
if ($tray -and $widget.Z -gt $tray.Z) { $result = "FAIL"; $notes += "widget is below the taskbar after the popup closed" }

Write-Host "$result ($Target)"
$notes | ForEach-Object { Write-Host "  $_" }
if ($result -eq "PASS") { exit 0 } else { exit 1 }
