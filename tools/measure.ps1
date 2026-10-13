<#
.SYNOPSIS
  Samples the running widget's CPU, memory, handles and GDI/USER objects, and checks them
  against a saved baseline (a leak test, not just a snapshot).

.DESCRIPTION
  Samples every 2 s for -Seconds while you (or another script) exercise the widget, e.g. the
  session simulator, or check-ui.ps1 in a loop. Prints start/peak/end values and writes them to
  -Save as JSON. With -Baseline, it fails (exit 1) if the end values exceed the baseline's end
  values by more than the budget: -MaxPrivateMB of private memory, -MaxHandles handles, and
  -MaxGuiObjects GDI and USER objects each. End values are taken after a forced-collection-free
  settle period, so a leak shows up as growth that doesn't come back.

.EXAMPLE
  .\tools\measure.ps1 -Seconds 120 -Save baseline.json
  .\tools\measure.ps1 -Seconds 120 -Baseline baseline.json -Save after.json
#>
param(
    [int]$Seconds = 60,
    [string]$Save = "",
    [string]$Baseline = "",
    [double]$MaxPrivateMB = 10,
    [int]$MaxHandles = 50,
    [int]$MaxGuiObjects = 20,
    [int]$SettleSeconds = 6
)

$ErrorActionPreference = "Stop"
Add-Type -Name Gui -Namespace Measure -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint flags);'

$proc = Get-Process ClaudeStatusBar -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "FAIL: the widget isn't running"; exit 1 }
$cores = [Environment]::ProcessorCount

function Sample($p, [double]$cpuPct) {
    $p.Refresh()
    [pscustomobject]@{
        cpuPct    = [math]::Round($cpuPct, 2)
        privateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        workingMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        handles   = $p.HandleCount
        gdi       = [Measure.Gui]::GetGuiResources($p.Handle, 0)
        user      = [Measure.Gui]::GetGuiResources($p.Handle, 1)
    }
}

$samples = New-Object System.Collections.Generic.List[object]
$lastCpu = $proc.TotalProcessorTime.TotalSeconds; $lastT = Get-Date
for ($t = 0; $t -lt $Seconds; $t += 2) {
    Start-Sleep -Seconds 2
    if ($proc.HasExited) { Write-Host "FAIL: the widget exited at ${t}s"; exit 1 }
    $proc.Refresh()
    $now = Get-Date; $cpu = $proc.TotalProcessorTime.TotalSeconds
    $samples.Add((Sample $proc (($cpu - $lastCpu) / ($now - $lastT).TotalSeconds / $cores * 100)))
    $lastCpu = $cpu; $lastT = $now
}

# Let transient objects (closed popups, finished timers) go before taking the end values.
Start-Sleep -Seconds $SettleSeconds
$end = Sample $proc 0

$summary = [ordered]@{
    seconds   = $Seconds
    start     = $samples[0]
    peak      = [pscustomobject]@{
        cpuPct    = ($samples | Measure-Object cpuPct -Maximum).Maximum
        privateMB = ($samples | Measure-Object privateMB -Maximum).Maximum
        workingMB = ($samples | Measure-Object workingMB -Maximum).Maximum
        handles   = ($samples | Measure-Object handles -Maximum).Maximum
        gdi       = ($samples | Measure-Object gdi -Maximum).Maximum
        user      = ($samples | Measure-Object user -Maximum).Maximum
    }
    avgCpuPct = [math]::Round(($samples | Measure-Object cpuPct -Average).Average, 2)
    end       = $end
}

"{0,-6} {1,8} {2,10} {3,10} {4,8} {5,5} {6,5}" -f "", "cpu%", "privateMB", "workingMB", "handles", "gdi", "user"
foreach ($k in "start", "peak", "end") {
    $v = $summary[$k]
    "{0,-6} {1,8} {2,10} {3,10} {4,8} {5,5} {6,5}" -f $k, $v.cpuPct, $v.privateMB, $v.workingMB, $v.handles, $v.gdi, $v.user
}
"avg cpu {0}%" -f $summary.avgCpuPct

if ($Save) { $summary | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $Save }

if ($Baseline) {
    $b = (Get-Content $Baseline -Raw | ConvertFrom-Json).end
    $checks = @(
        @{ name = "private memory"; delta = $end.privateMB - $b.privateMB; max = $MaxPrivateMB; unit = "MB" },
        @{ name = "handles"; delta = $end.handles - $b.handles; max = $MaxHandles; unit = "" },
        @{ name = "GDI objects"; delta = $end.gdi - $b.gdi; max = $MaxGuiObjects; unit = "" },
        @{ name = "USER objects"; delta = $end.user - $b.user; max = $MaxGuiObjects; unit = "" }
    )
    $failed = $false
    foreach ($c in $checks) {
        $ok = $c.delta -le $c.max
        if (-not $ok) { $failed = $true }
        "{0} {1}: {2:+0.#;-0.#;0}{3} vs baseline (budget {4}{3})" -f $(if ($ok) { "ok  " } else { "FAIL" }), $c.name, $c.delta, $c.unit, $c.max
    }
    if ($failed) { exit 1 }
}
exit 0
