@echo off
rem Double-click to install Sketch ProMax for your Windows user (no admin needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
    echo.
    echo If Windows could not start PowerShell here, open PowerShell yourself ^(Start menu, type PowerShell^) and paste these two lines:
    echo     Set-ExecutionPolicy -Scope Process Bypass -Force
    echo     ^& "%~dp0install.ps1"
)
pause
