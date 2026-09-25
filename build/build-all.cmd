@echo off
setlocal
cd /d "%~dp0"
echo ========================================
echo  CleanC one-click build 1.6.9
echo ========================================
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0build-local.ps1"
