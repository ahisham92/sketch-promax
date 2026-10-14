# Builds Sketch ProMax for every supported Revit version, plus the MCP server for the Claude desktop app.
# Output goes to .\dist\ :
#   dist\Revit2024\, dist\Revit2025\, dist\Revit2026\   add-in builds
#   dist\mcp\SketchProMax.Mcp.exe                       self-contained MCP server (no .NET install needed)
# Usage: .\build.ps1 [-Configuration Release] [-Versions 2024,2025,2026]
param(
    [string]$Configuration = "Release",
    [int[]]$Versions = @(2024, 2025, 2026)
)
$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "dist"
$addin = Join-Path $PSScriptRoot "src\SketchProMax\SketchProMax.csproj"
$mcp = Join-Path $PSScriptRoot "src\SketchProMax.Mcp\SketchProMax.Mcp.csproj"

foreach ($v in $Versions) {
    Write-Host "== Revit $v" -ForegroundColor Cyan
    dotnet build $addin -c $Configuration -p:RevitVersion=$v
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $v" }
    $out = Join-Path $PSScriptRoot "src\SketchProMax\bin\$Configuration\Revit$v"
    $target = Join-Path $dist "Revit$v"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $out $target -Recurse
}

Write-Host "== MCP server" -ForegroundColor Cyan
dotnet publish $mcp -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $dist "mcp")
if ($LASTEXITCODE -ne 0) { throw "MCP server publish failed" }
Write-Host "Done. Run .\install.ps1 to install." -ForegroundColor Green
