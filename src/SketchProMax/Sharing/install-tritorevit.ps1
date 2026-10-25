# Installs the shared Revit tools in this folder for the current Windows user (no admin rights needed), and the
# TritoRevit add-in that holds them if it isn't installed yet.
#   1. The TritoRevit add-in, for each installed Revit (2024 to 2027): TritoRevit.addin and TritoRevit\TritoRevit.dll in
#      %APPDATA%\Autodesk\Revit\Addins\<year>, and the tools' engine in %LOCALAPPDATA%\TritoRevit\versions\<version>.
#      Downloaded once from the add-in's releases on GitHub; later versions arrive by themselves while Revit runs.
#   2. The pack (the "pack" folder here) in %APPDATA%\TritoRevit\packs\<id>. A later revision is never replaced by an
#      older zip. Its tools then update by themselves from the folder they were shared to.
# The first install needs Revit to be restarted once; after that, new packs and updates appear with Revit open.
$ErrorActionPreference = "Stop"
$repository = "ahisham92/sketch-promax"
# Run as a file (Install TritoRevit.cmd) or pasted as text in the extracted folder ("iex (Get-Content -Raw .\install-tritorevit.ps1)"),
# for PCs whose policy blocks .cmd files or scripts: then there is no script path and the current folder is the zip's.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }
$packSource = Join-Path $here "pack"
if (-not (Test-Path (Join-Path $packSource "pack.json"))) { throw "There is no pack folder in $here. Extract the whole zip first (right-click > Extract All), then run the installer from the extracted folder (see README.txt)." }
if ($ExecutionContext.SessionState.LanguageMode -ne "FullLanguage") {
    throw "This PC's PowerShell is locked down by company policy (Constrained Language mode), so TritoRevit can't be installed by its user. Ask IT to install it, or to allow PowerShell for this folder."
}

# Files from a downloaded zip carry a "came from the internet" mark that makes Windows and Revit 2024 refuse to load them.
function Clear-DownloadMark($path) {
    if (Test-Path $path) { Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue }
}
Clear-DownloadMark $here

$pack = Get-Content (Join-Path $packSource "pack.json") -Raw | ConvertFrom-Json
if (-not ($pack.id -match '^[a-z0-9][a-z0-9-]{0,47}$')) { throw "The pack's id '$($pack.id)' is not valid. Ask for the zip again." }

$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$local = Join-Path $env:LOCALAPPDATA "TritoRevit"
$versions = Join-Path $local "versions"
$years = @(2024, 2025, 2026, 2027 | Where-Object { Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $_") })
if ($years.Count -eq 0) { throw "No supported Revit (2024 to 2027) was found under $env:ProgramFiles\Autodesk. Install Revit, then run this again." }
$revitRunning = [bool](Get-Process Revit -ErrorAction SilentlyContinue)

function Get-Hash($path) { if (Test-Path $path) { (Get-FileHash $path -Algorithm SHA256).Hash } else { "" } }

# An engine for this Revit version: any finished version folder with its SketchProMax.dll.
function Test-Engine($year) {
    if (-not (Test-Path $versions)) { return $false }
    foreach ($v in Get-ChildItem $versions -Directory) {
        if ($v.Name -notmatch '^\d+\.\d+\.\d+$') { continue }
        if (Test-Path (Join-Path $v.FullName "Revit$year\SketchProMax.dll")) { return $true }
    }
    return $false
}
$needs = @($years | Where-Object {
    -not (Test-Engine $_) -or -not (Test-Path (Join-Path $addinsRoot "$_\TritoRevit\TritoRevit.dll")) -or -not (Test-Path (Join-Path $addinsRoot "$_\TritoRevit.addin"))
})
$firstInstall = @($years | Where-Object { -not (Test-Path (Join-Path $addinsRoot "$_\TritoRevit.addin")) })

# The latest release: GitHub's API first, then the release page's redirect (the API allows 60 requests an hour per office network).
function Get-LatestRelease {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try {
        $release = Invoke-RestMethod -UseBasicParsing -TimeoutSec 15 -Headers @{ "User-Agent" = "TritoRevit-installer" } "https://api.github.com/repos/$repository/releases/latest"
        $version = $release.tag_name.TrimStart('v', 'V')
        $asset = $release.assets | Where-Object { $_.name -like "SketchProMax-*.zip" } | Select-Object -First 1
        if ($asset) { return [pscustomobject]@{ Version = $version; Url = $asset.browser_download_url } }
        return [pscustomobject]@{ Version = $version; Url = "https://github.com/$repository/releases/download/$($release.tag_name)/SketchProMax-$version.zip" }
    }
    catch { }
    $request = [Net.HttpWebRequest]::Create("https://github.com/$repository/releases/latest")
    $request.AllowAutoRedirect = $false
    $request.UserAgent = "TritoRevit-installer"
    $response = $request.GetResponse()
    $location = $response.Headers["Location"]
    $response.Close()
    if (-not ($location -match '/releases/tag/([^/?#]+)')) { throw "GitHub did not say which release is the latest." }
    $tag = [Uri]::UnescapeDataString($Matches[1])
    $version = $tag.TrimStart('v', 'V')
    return [pscustomobject]@{ Version = $version; Url = "https://github.com/$repository/releases/download/$tag/SketchProMax-$version.zip" }
}

# Copies a file only when it differs. A DLL Revit has loaded can be renamed but not overwritten: it is moved aside.
function Copy-IfChanged($from, $to) {
    if ((Get-Hash $from) -eq (Get-Hash $to)) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
    if (Test-Path $to) {
        try { Copy-Item $from $to -Force -ErrorAction Stop; return }
        catch { Move-Item $to "$to.$([DateTime]::UtcNow.Ticks).old" -Force }
    }
    Copy-Item $from $to -Force
}

# The add-in Revit loads: always the same path, replaced only by a higher TritoRevit version. Revit asks "Always Load"
# again for an unsigned add-in whose file changed, so a rebuild of the same version (other bytes) is never copied over it.
function Copy-Loader($from, $to) {
    if (-not (Test-Path $to)) { Copy-IfChanged $from $to; return }
    $new = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($from).FileVersion)
    $old = $null
    try { $old = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($to).FileVersion) } catch { }
    if ($null -eq $old -or $new -gt $old) { Copy-IfChanged $from $to }
}

$installedYears = @()
if ($needs.Count -gt 0) {
    Write-Host "Downloading the TritoRevit add-in for Revit $($needs -join ', ')..." -ForegroundColor Cyan
    try { $latest = Get-LatestRelease }
    catch { throw "Couldn't reach GitHub to download the TritoRevit add-in ($($_.Exception.Message)). Connect to the internet and run this again." }
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("TritoRevit-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    try {
        $zip = Join-Path $temp "release.zip"
        $old = $ProgressPreference
        $ProgressPreference = "SilentlyContinue" # the progress bar makes the download many times slower in Windows PowerShell
        try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 600 -Headers @{ "User-Agent" = "TritoRevit-installer" } $latest.Url -OutFile $zip }
        finally { $ProgressPreference = $old }
        Unblock-File $zip -ErrorAction SilentlyContinue
        Expand-Archive $zip (Join-Path $temp "x") -Force
        $dist = Get-ChildItem (Join-Path $temp "x") -Directory -Recurse -Filter "dist" | Select-Object -First 1
        if (-not $dist) { throw "The downloaded release has no dist folder." }
        Clear-DownloadMark $dist.FullName
        $target = Join-Path $versions $latest.Version
        foreach ($year in $needs) {
            $build = Join-Path $dist.FullName "Revit$year"
            if (-not (Test-Path (Join-Path $build "SketchProMax.dll"))) { Write-Warning "The release has no build for Revit $year; skipped."; continue }
            if (-not (Test-Path (Join-Path $build "TritoRevit\TritoRevit.dll"))) { Write-Warning "The release $($latest.Version) has no TritoRevit for Revit $year; skipped."; continue }
            # The engine: unpacked beside its final place, then renamed, so TritoRevit never starts half a copy.
            $engine = Join-Path $target "Revit$year"
            if (-not (Test-Path (Join-Path $engine "SketchProMax.dll"))) {
                $partial = "$engine.partial"
                if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
                New-Item -ItemType Directory -Force -Path $partial | Out-Null
                Get-ChildItem $build | Where-Object { $_.Name -ne "TritoRevit" -and $_.Name -ne "SketchProMax.addin" } | Copy-Item -Destination $partial -Recurse -Force
                if (Test-Path $engine) { Remove-Item $engine -Recurse -Force }
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                Move-Item $partial $engine
            }
            $addins = Join-Path $addinsRoot "$year"
            Copy-Loader (Join-Path $build "TritoRevit\TritoRevit.dll") (Join-Path $addins "TritoRevit\TritoRevit.dll")
            Copy-IfChanged (Join-Path $build "TritoRevit\TritoRevit.addin") (Join-Path $addins "TritoRevit.addin")
            Clear-DownloadMark (Join-Path $addins "TritoRevit")
            Unblock-File (Join-Path $addins "TritoRevit.addin") -ErrorAction SilentlyContinue
            $installedYears += $year
            Write-Host "Installed TritoRevit $($latest.Version) for Revit $year" -ForegroundColor Green
        }
    }
    finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

# The pack: copied beside its place and swapped in, so TritoRevit never reads half a pack. A newer one already here stays.
$packs = Join-Path $env:APPDATA "TritoRevit\packs"
$targetPack = Join-Path $packs $pack.id
$current = Join-Path $targetPack "pack.json"
$keep = $false
if (Test-Path $current) {
    try { $installedPack = Get-Content $current -Raw | ConvertFrom-Json; $keep = [int]$installedPack.revision -gt [int]$pack.revision } catch { }
}
if ($keep) {
    Write-Host "'$($pack.name)' revision $($installedPack.revision) is already installed, newer than this zip (revision $($pack.revision)); it was kept." -ForegroundColor Yellow
} else {
    New-Item -ItemType Directory -Force -Path $packs | Out-Null
    $incoming = "$targetPack.incoming"
    if (Test-Path $incoming) { Remove-Item $incoming -Recurse -Force }
    Copy-Item $packSource $incoming -Recurse -Force
    $oldPack = "$targetPack.old"
    if (Test-Path $oldPack) { Remove-Item $oldPack -Recurse -Force }
    if (Test-Path $targetPack) { Move-Item $targetPack $oldPack }
    Move-Item $incoming $targetPack
    if (Test-Path $oldPack) { Remove-Item $oldPack -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "Installed '$($pack.name)' (revision $($pack.revision), $(@($pack.tools).Count) tool(s))" -ForegroundColor Green
}

Write-Host ""
Write-Host "==== TritoRevit ====" -ForegroundColor Cyan
foreach ($t in @($pack.tools)) { Write-Host "  - $($t.title)" }
Write-Host ""
$restart = @($firstInstall | Where-Object { $installedYears -contains $_ })
if ($restart.Count -gt 0) {
    if ($revitRunning) { Write-Host "Close and reopen Revit once: the tools are then on the TritoRevit tab." -ForegroundColor Yellow }
    else { Write-Host "Open Revit: the tools are on the TritoRevit tab." }
    Write-Host "If Revit asks about loading TritoRevit, choose 'Always Load'."
} else {
    Write-Host "The tools appear on the TritoRevit tab within a few seconds; Revit can stay open."
}
if ($pack.updateFrom) { Write-Host "They update by themselves from $($pack.updateFrom) while Revit is open." }
