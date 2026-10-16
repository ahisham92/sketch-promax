# Installs Sketch ProMax for the current Windows user (no admin rights needed):
#   1. Copies the add-in into each installed Revit version's add-ins folder (2024 to 2027). The manifest
#      (SketchProMax.addin) loads SketchProMax.Loader.dll, which loads the newest installed SketchProMax.dll and can
#      switch to an update without restarting Revit.
#   2. Copies the MCP server to %LOCALAPPDATA%\SketchProMax\mcp.
#   3. Registers it with the Claude desktop app, if installed, so you can chat at no extra cost.
# Run .\build.ps1 first. Close Revit and the Claude desktop app before installing.
$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $dist)) { throw "Run .\build.ps1 first." }

# Files from a downloaded zip carry a "came from the internet" mark that makes Windows and
# Revit 2024 (.NET Framework) refuse to load them. Clear it: on the release zip itself (next to this folder or in
# Downloads, so a later re-extract is clean too), on every extracted file, and again on the installed copies below.
function Clear-DownloadMark($path) {
    if (Test-Path $path) { Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue }
}
# With no zip found $zips is $null, and piping $null would hand Unblock-File a null path (an error under "Stop").
$downloads = if ($env:USERPROFILE) { Join-Path $env:USERPROFILE "Downloads" } else { $null }
$zips = @($PSScriptRoot, (Split-Path $PSScriptRoot -Parent), $downloads) |
    Where-Object { $_ -and (Test-Path $_) } |
    ForEach-Object { Get-ChildItem $_ -Filter "SketchProMax-*.zip" -File -ErrorAction SilentlyContinue }
if ($zips) { $zips | Unblock-File -ErrorAction SilentlyContinue }
Clear-DownloadMark $PSScriptRoot

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Warning "Revit is running. Files it has loaded can't be replaced; close Revit and run this again if anything below fails."
}
$installed = @()
$registered = @()

foreach ($v in 2024, 2025, 2026, 2027) {
    $src = Join-Path $dist "Revit$v"
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    if (-not (Test-Path $src)) { continue }
    if (-not (Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $v"))) { Write-Host "Revit $v not found, skipping."; continue }
    New-Item -ItemType Directory -Force -Path (Join-Path $addins "SketchProMax") | Out-Null
    if (-not (Test-Path (Join-Path $src "SketchProMax.Loader.dll"))) { throw "dist\Revit$v has no SketchProMax.Loader.dll. Run .\build.ps1 again." }
    # Files first, manifest last, so Revit never finds a manifest naming a loader that isn't there yet.
    Get-ChildItem $src -Exclude "SketchProMax.addin" | Copy-Item -Destination (Join-Path $addins "SketchProMax") -Recurse -Force
    Copy-Item (Join-Path $src "SketchProMax.addin") $addins -Force
    Clear-DownloadMark (Join-Path $addins "SketchProMax")
    Unblock-File (Join-Path $addins "SketchProMax.addin") -ErrorAction SilentlyContinue
    $installed += [pscustomobject]@{ Revit = $v; Folder = (Join-Path $addins "SketchProMax") }
    Write-Host "Installed add-in for Revit $v" -ForegroundColor Green
}

$mcpDir = Join-Path $env:LOCALAPPDATA "SketchProMax\mcp"
New-Item -ItemType Directory -Force -Path $mcpDir | Out-Null
$exe = Join-Path $mcpDir "SketchProMax.Mcp.exe"
# The Claude app keeps the connector running while it is open, which locks the file. Stop our own connector (only
# SketchProMax.Mcp.exe; the Claude app starts it again when it next starts), then copy, retrying while Windows lets go.
Get-Process SketchProMax.Mcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$copied = $false
for ($try = 1; $try -le 5 -and -not $copied; $try++) {
    try {
        Copy-Item (Join-Path $dist "mcp\*") $mcpDir -Recurse -Force -ErrorAction Stop
        $copied = $true
    }
    catch {
        if ($try -eq 5) { break }
        Get-Process SketchProMax.Mcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}
if ($copied) {
    Clear-DownloadMark $mcpDir
    Write-Host "Installed MCP server: $exe" -ForegroundColor Green
} else {
    Write-Warning "The Claude connector is in use and was not updated. Quit the Claude app (right-click its icon by the clock, Quit) and run Install.cmd again. The Revit add-in above is installed."
}

# The Claude desktop app keeps its config in %APPDATA%\Claude, or, for the Microsoft Store / MSIX build,
# in %LOCALAPPDATA%\Packages\<package>\LocalCache\Roaming\Claude. Register in every place that exists, and
# always in %APPDATA%\Claude so a later install of the app finds it too.
$claudeDirs = @(Join-Path $env:APPDATA "Claude")
$packages = Join-Path $env:LOCALAPPDATA "Packages"
if (Test-Path $packages) {
    # The package folder is named after the app's package family, e.g. Claude_pzs8sxrjxfjjc or AnthropicPBC.Claude_fnn82j28hfe8t.
    Get-ChildItem $packages -Directory | Where-Object { $_.Name -match '(^|\.)Claude_[a-z0-9]+$' } | ForEach-Object {
        $claudeDirs += Join-Path $_.FullName "LocalCache\Roaming\Claude"
    }
}

foreach ($claudeDir in $claudeDirs) {
    New-Item -ItemType Directory -Force -Path $claudeDir | Out-Null
    $configPath = Join-Path $claudeDir "claude_desktop_config.json"
    $config = [pscustomobject]@{}
    if (Test-Path $configPath) {
        $text = Get-Content $configPath -Raw
        if ($text -and $text.Trim()) {
            try { $config = $text | ConvertFrom-Json }
            catch { Write-Warning "Skipping $configPath because it isn't valid JSON. Fix it, then run this again."; continue }
        }
        Copy-Item $configPath "$configPath.bak" -Force
    }
    if (-not $config.PSObject.Properties["mcpServers"]) { $config | Add-Member mcpServers ([pscustomobject]@{}) }
    $config.mcpServers | Add-Member -Force "sketch-promax" ([pscustomobject]@{ command = $exe })
    # Write without a byte-order mark; the Claude app rejects JSON that starts with one.
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
    $registered += $configPath
    Write-Host "Registered with the Claude desktop app: $configPath" -ForegroundColor Green
}

# First-run checklist.
Write-Host ""
Write-Host "==== Sketch ProMax: first-run checklist ====" -ForegroundColor Cyan
if ($installed.Count -gt 0) {
    foreach ($i in $installed) { Write-Host "  [x] Revit $($i.Revit): add-in installed in $($i.Folder)" }
} else {
    Write-Host "  [ ] No supported Revit (2024 to 2027) was found under $env:ProgramFiles\Autodesk. Install Revit, then run this again." -ForegroundColor Yellow
}
Write-Host "  [x] MCP server: $exe"
if ($registered.Count -gt 0) {
    foreach ($r in $registered) { Write-Host "  [x] Claude desktop config written: $r" }
} else {
    Write-Host "  [ ] Claude desktop config was not written (see the warnings above)." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Quit the Claude desktop app completely (system tray > Quit) and open it again to load the Revit tools."
Write-Host "  2. Open Revit$(if ($installed.Count -gt 0) { ' ' + (($installed | ForEach-Object { $_.Revit }) -join ' / ') }) and a model. If Revit asks about loading Sketch ProMax, choose 'Always Load'."
Write-Host "  3. On the Sketch ProMax ribbon tab click Claude > Status: it should say 'Ready' or 'Connected'. Then ask Claude something about the model."
