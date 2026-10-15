# Builds ClaudeStatusBar.exe and stages the hook scripts and setup helpers under .\dist.
#
#   .\build.ps1                  Offline build (default): no network code compiled in
#   .\build.ps1 -Online          Adds the opt-in usage fetch from Anthropic's usage endpoint
#   .\build.ps1 -SelfContained   Bundles the runtime; the exe runs without a separate install
#   .\build.ps1 -Install         Builds, then registers the Claude Code hooks for the current user
#   .\build.ps1 -Zip             Builds, then packages .\dist into a release zip
#
param(
    [switch]$Online,
    [switch]$SelfContained,
    [switch]$Install,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$selfContainedValue = $SelfContained.IsPresent.ToString().ToLower()
$offlineValue = (-not $Online).ToString().ToLower()
$variant = if ($Online) { "online" } else { "offline" }

Write-Host "Publishing ClaudeStatusBar.exe ($variant build, self-contained: $selfContainedValue)..."
dotnet publish src/ClaudeStatusBar.csproj `
    -c Release `
    -r win-x64 `
    --self-contained $selfContainedValue `
    -p:Offline=$offlineValue `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Stage the hook scripts and one-click setup helpers next to the exe.
New-Item -ItemType Directory -Force -Path dist/hooks | Out-Null
Copy-Item hooks/*.js dist/hooks/ -Force
Copy-Item setup.bat, uninstall.bat dist/ -Force

$exe = Resolve-Path dist/ClaudeStatusBar.exe
Write-Host ""
Write-Host "Built: $exe ($variant)"

if ($Install) {
    Write-Host "Registering Claude Code hooks..."
    node dist/hooks/install.js "$exe"
}

if ($Zip) {
    $zipPath = if ($Online) { "dist/ClaudeStatusBar-win-x64-online.zip" } else { "dist/ClaudeStatusBar-win-x64.zip" }
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path dist/ClaudeStatusBar.exe, dist/hooks, dist/setup.bat, dist/uninstall.bat -DestinationPath $zipPath
    Write-Host "Packaged: $(Resolve-Path $zipPath)"
}

if (-not $Install) {
    Write-Host "Install hooks with:  node dist\hooks\install.js `"$exe`"   (or run dist\setup.bat)"
}
