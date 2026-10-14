# Installs Sketch ProMax for the current Windows user (no admin rights needed):
#   1. Copies the add-in into each installed Revit version's add-ins folder (2024 to 2027).
#   2. Copies the MCP server to %LOCALAPPDATA%\SketchProMax\mcp.
#   3. Registers it with the Claude desktop app, if installed, so you can chat at no extra cost.
# Run .\build.ps1 first. Close Revit and the Claude desktop app before installing.
$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $dist)) { throw "Run .\build.ps1 first." }

# Files from a downloaded zip carry a "came from the internet" mark that makes Windows and
# Revit 2024 (.NET Framework) refuse to load them. Clear it.
Get-ChildItem $PSScriptRoot -Recurse -File | Unblock-File

foreach ($v in 2024, 2025, 2026, 2027) {
    $src = Join-Path $dist "Revit$v"
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    if (-not (Test-Path $src)) { continue }
    if (-not (Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $v"))) { Write-Host "Revit $v not found, skipping."; continue }
    New-Item -ItemType Directory -Force -Path (Join-Path $addins "SketchProMax") | Out-Null
    Copy-Item (Join-Path $src "SketchProMax.addin") $addins -Force
    Get-ChildItem $src -Exclude "SketchProMax.addin" | Copy-Item -Destination (Join-Path $addins "SketchProMax") -Recurse -Force
    Write-Host "Installed add-in for Revit $v" -ForegroundColor Green
}

$mcpDir = Join-Path $env:LOCALAPPDATA "SketchProMax\mcp"
New-Item -ItemType Directory -Force -Path $mcpDir | Out-Null
Copy-Item (Join-Path $dist "mcp\*") $mcpDir -Recurse -Force
$exe = Join-Path $mcpDir "SketchProMax.Mcp.exe"
Write-Host "Installed MCP server: $exe" -ForegroundColor Green

# The Claude desktop app keeps its config in %APPDATA%\Claude, or, for the Microsoft Store / MSIX build,
# in %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude. Register in every place that exists, and
# always in %APPDATA%\Claude so a later install of the app finds it too.
$claudeDirs = @(Join-Path $env:APPDATA "Claude")
$packages = Join-Path $env:LOCALAPPDATA "Packages"
if (Test-Path $packages) {
    Get-ChildItem $packages -Directory -Filter "Claude*" | ForEach-Object {
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
    Write-Host "Registered with the Claude desktop app: $configPath" -ForegroundColor Green
}
Write-Host "Quit the Claude desktop app completely (system tray > Quit) and open it again to load the Revit tools." -ForegroundColor Green
