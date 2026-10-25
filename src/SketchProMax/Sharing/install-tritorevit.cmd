@echo off
rem Double-click to install TritoRevit and the shared tools in this folder, for your Windows user (no admin needed).
rem The script is read as text rather than run as a file, so a policy that only allows signed scripts doesn't stop it.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex (Get-Content -Raw -LiteralPath '.\install-tritorevit.ps1')"
pause
