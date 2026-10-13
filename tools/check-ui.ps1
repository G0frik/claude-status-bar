<#
.SYNOPSIS
  Checks the running widget's popups and clicks with real mouse input.

.DESCRIPTION
  usage, dots, menu: hovers a part of the widget (right-clicks it for the menu), waits several
  400 ms poll ticks, then inspects the window z-order: the popup must sit above the widget, and a
  tooltip must not overlap the pill. Finally checks that the widget is back above the taskbar
  once the popup has closed.

  open: puts another window (Notepad) in front, left-clicks the widget (-At label or dot), and
  checks that the foreground window becomes the session's window (title matching -ExpectTitle).

  Saves screenshots on request and restores the cursor. Exits 0 on PASS, 1 on FAIL.

.EXAMPLE
  .\tools\check-ui.ps1 -Target usage
  .\tools\check-ui.ps1 -Target dots -Screenshot dots.png
  .\tools\check-ui.ps1 -Target menu
  .\tools\check-ui.ps1 -Target open -At label -ExpectTitle "claude-status-bar"
#>
param(
    [ValidateSet("usage", "dots", "menu", "open")] [string]$Target = "usage",
    [ValidateSet("label", "dot")] [string]$At = "label",
    [string]$ExpectTitle = "",
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
    "dots"  { [System.Drawing.Point]::new($right - [int]((17 + 7) * $scale), $midY) }
    "menu"  { [System.Drawing.Point]::new($right - [int](120 * $scale), $midY) }
    "open"  {
        if ($At -eq "dot") { [System.Drawing.Point]::new($right - [int]((17 + 7) * $scale), $midY) }
        else { [System.Drawing.Point]::new($right - [int](120 * $scale), $midY) }
    }
}

if ($Target -eq "open") {
    $original = [System.Windows.Forms.Cursor]::Position
    $other = Start-Process notepad -PassThru
    try {
        Start-Sleep -Milliseconds 1200
        $before = [ZOrder]::Title([ZOrder]::GetForegroundWindow())
        [System.Windows.Forms.Cursor]::Position = $point
        Start-Sleep -Milliseconds 150
        [ZOrder]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [ZOrder]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 1000
        $after = [ZOrder]::Title([ZOrder]::GetForegroundWindow())
    }
    finally {
        [System.Windows.Forms.Cursor]::Position = $original
        Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue
    }
    $ok = $after -and $after -notmatch "Notepad" -and (-not $ExpectTitle -or $after -like "*$ExpectTitle*")
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
            $result = "FAIL"; $notes += "tooltip overlaps the pill by $($pr.B - $pillTop)px"
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
