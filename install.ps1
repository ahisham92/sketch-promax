# Installs Sketch ProMax for the current Windows user (no admin rights needed):
#   1. Copies the add-in into each installed Revit version's add-ins folder (2024, 2025, 2026).
#   2. Copies the MCP server to %LOCALAPPDATA%\SketchProMax\mcp.
#   3. Registers it with the Claude desktop app, if installed, so you can chat at no extra cost.
# Run .\build.ps1 first. Close Revit and the Claude desktop app before installing.
$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $dist)) { throw "Run .\build.ps1 first." }

foreach ($v in 2024, 2025, 2026) {
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

$claudeDir = Join-Path $env:APPDATA "Claude"
if (Test-Path $claudeDir) {
    $configPath = Join-Path $claudeDir "claude_desktop_config.json"
    $config = if (Test-Path $configPath) { Get-Content $configPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $config.PSObject.Properties["mcpServers"]) { $config | Add-Member mcpServers ([pscustomobject]@{}) }
    $config.mcpServers | Add-Member -Force "sketch-promax" ([pscustomobject]@{ command = $exe })
    if (Test-Path $configPath) { Copy-Item $configPath "$configPath.bak" -Force }
    # Write without a byte-order mark; the Claude app rejects JSON that starts with one.
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
    Write-Host "Registered with the Claude desktop app. Restart it to load the Revit tools." -ForegroundColor Green
} else {
    Write-Host "Claude desktop app not found. Install it from https://claude.ai/download, then run this script again."
}
