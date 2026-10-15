<#
.SYNOPSIS
  Checks that a ClaudeStatusBar.exe (or .dll) is the offline build: no network code compiled in.

.DESCRIPTION
  Works on a published single-file exe, including the self-contained release, without extracting it:
    - Product name (Explorer > Properties > Details) must be "Claude Status Bar (offline build)".
    - The file must contain neither ".credentials.json" nor "api.anthropic.com", in UTF-8 or UTF-16.
      Only the online build's usage fetch uses them, so the offline build can't read the Claude
      Code credentials file or call that endpoint.
  It does not look for "System.Net.Http": a self-contained exe bundles the whole .NET runtime,
  which includes the networking libraries whether the app uses them or not. That the app's own
  assembly references no System.Net code is checked by tests/ClaudeStatusBar.Tests
  (BuildVariantTests), which inspect its metadata.
  Prints the SHA-256 to compare with the release's SHA256SUMS.txt. Exits 0 on PASS, 1 on FAIL.

.EXAMPLE
  .\tools\verify-offline.ps1 dist\ClaudeStatusBar.exe
#>
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = "Stop"
$file = Get-Item $Path
$failures = @()

$product = $file.VersionInfo.ProductName
if ($product -ne "Claude Status Bar (offline build)") { $failures += "product name is '$product'" }

# Latin-1 maps every byte to one char, so a byte search becomes an ordinal string search.
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$text = $latin1.GetString([System.IO.File]::ReadAllBytes($file.FullName))
foreach ($needle in ".credentials.json", "api.anthropic.com") {
    foreach ($encoding in [System.Text.Encoding]::UTF8, [System.Text.Encoding]::Unicode) {
        $pattern = $latin1.GetString($encoding.GetBytes($needle))
        if ($text.IndexOf($pattern, [System.StringComparison]::Ordinal) -ge 0) {
            $failures += "contains '$needle' ($($encoding.EncodingName))"
        }
    }
}

$hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLower()
Write-Host "$($file.Name)  sha256 $hash"
Write-Host "product: $product"
if ($failures) {
    Write-Host "FAIL: not the offline build"
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "PASS: offline build (no credentials file name, no usage endpoint)"
exit 0
