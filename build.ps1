# Builds Sketch ProMax for every supported Revit version, plus the MCP server for the Claude desktop app.
# Output goes to .\dist\ :
#   dist\Revit2024\ ... dist\Revit2027\   add-in builds: SketchProMax.Loader.dll (what Revit loads, named by
#                                       SketchProMax.addin) next to the core SketchProMax.dll and its dependencies
#                                       and TritoRevit\ (TritoRevit.dll and TritoRevit.addin: the add-in for shared tools,
#                                       which the zips Share tools makes install for people without Sketch ProMax)
#   dist\mcp\SketchProMax.Mcp.exe                       self-contained MCP server (no .NET install needed)
# Usage: .\build.ps1 [-Configuration Release] [-Versions 2024,2025,2026,2027]
param(
    [string]$Configuration = "Release",
    [int[]]$Versions = @(2024, 2025, 2026, 2027)
)
$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "dist"
$addin = Join-Path $PSScriptRoot "src\SketchProMax\SketchProMax.csproj"
$mcp = Join-Path $PSScriptRoot "src\SketchProMax.Mcp\SketchProMax.Mcp.csproj"
$trito = Join-Path $PSScriptRoot "src\TritoRevit\TritoRevit.csproj"

foreach ($v in $Versions) {
    Write-Host "== Revit $v" -ForegroundColor Cyan
    dotnet build $addin -c $Configuration -p:RevitVersion=$v
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $v" }
    $out = Join-Path $PSScriptRoot "src\SketchProMax\bin\$Configuration\Revit$v"
    # The core references the loader, so its build carries it; an update without it could not switch over live.
    foreach ($f in "SketchProMax.dll", "SketchProMax.Loader.dll", "SketchProMax.addin") {
        if (-not (Test-Path (Join-Path $out $f))) { throw "Revit $v build is missing $f" }
    }
    $target = Join-Path $dist "Revit$v"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $out $target -Recurse
    dotnet build $trito -c $Configuration -p:RevitVersion=$v
    if ($LASTEXITCODE -ne 0) { throw "TritoRevit build failed for Revit $v" }
    $tritoOut = Join-Path $PSScriptRoot "src\TritoRevit\bin\$Configuration\Revit$v"
    $tritoTarget = Join-Path $target "TritoRevit"
    New-Item -ItemType Directory -Force $tritoTarget | Out-Null
    foreach ($f in "TritoRevit.dll", "TritoRevit.addin") {
        if (-not (Test-Path (Join-Path $tritoOut $f))) { throw "TritoRevit build for Revit $v is missing $f" }
        Copy-Item (Join-Path $tritoOut $f) $tritoTarget
    }
}

Write-Host "== MCP server" -ForegroundColor Cyan
dotnet publish $mcp -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $dist "mcp")
if ($LASTEXITCODE -ne 0) { throw "MCP server publish failed" }
Write-Host "Done. Run .\install.ps1 to install." -ForegroundColor Green
