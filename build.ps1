# Builds Sketch ProMax for every supported Revit version.
# Usage: .\build.ps1 [-Configuration Release] [-Versions 2024,2025,2026]
param(
    [string]$Configuration = "Release",
    [int[]]$Versions = @(2024, 2025, 2026)
)
$ErrorActionPreference = "Stop"
foreach ($v in $Versions) {
    Write-Host "== Revit $v" -ForegroundColor Cyan
    dotnet build "$PSScriptRoot\src\SketchProMax\SketchProMax.csproj" -c $Configuration -p:RevitVersion=$v
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $v" }
}
