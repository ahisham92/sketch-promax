# Installs the latest Sketch ProMax release in one line, with no .cmd file to double-click (for PCs where Windows
# refuses to run Install.cmd, such as a zip marked "came from the internet" or a policy that blocks .cmd files or
# starting powershell.exe from another program). Paste this into PowerShell:
#
#   irm https://raw.githubusercontent.com/ahisham92/sketch-promax/main/get.ps1 | iex
#
# It downloads the latest release zip into %TEMP%, clears its download mark, extracts it and runs its install.ps1
# in this same PowerShell window (no new powershell.exe; for the current user, no admin needed). Close Revit and the
# Claude desktop app first.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Windows PowerShell downloads far slower while it draws progress
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
# Company proxies often want the Windows sign-in.
if ([Net.WebRequest]::DefaultWebProxy) { [Net.WebRequest]::DefaultWebProxy.Credentials = [Net.CredentialCache]::DefaultNetworkCredentials }

$repo = "ahisham92/sketch-promax"
Write-Host "Finding the latest Sketch ProMax release..."
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ "User-Agent" = "SketchProMax-get" } -UseBasicParsing
$asset = $release.assets | Where-Object { $_.name -like "SketchProMax-*.zip" } | Select-Object -First 1
if (-not $asset) { throw "The latest release ($($release.tag_name)) has no SketchProMax zip. Download it from https://github.com/$repo/releases instead." }

$work = Join-Path $env:TEMP ("SketchProMax-install-" + $release.tag_name.TrimStart("v"))
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work $asset.name
if (-not ((Test-Path $zip) -and (Get-Item $zip).Length -eq $asset.size)) {
    Write-Host ("Downloading {0} ({1:N0} MB)..." -f $asset.name, ($asset.size / 1MB))
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ "User-Agent" = "SketchProMax-get" }
}
Unblock-File $zip -ErrorAction SilentlyContinue

$out = Join-Path $work "extracted"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host "Extracting..."
Expand-Archive $zip -DestinationPath $out -Force
Get-ChildItem $out -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$install = Get-ChildItem $out -Recurse -Filter "install.ps1" -File | Select-Object -First 1
if (-not $install) { throw "The zip has no install.ps1. Download it from https://github.com/$repo/releases instead." }
Write-Host "Installing Sketch ProMax $($release.tag_name.TrimStart('v'))..."
# In this window: some company PCs refuse to start powershell.exe from another program. Bypass lasts for this window only.
Set-ExecutionPolicy -Scope Process Bypass -Force
try { & $install.FullName }
catch { throw "install.ps1 stopped: $($_.Exception.Message) The files are in $out." }
