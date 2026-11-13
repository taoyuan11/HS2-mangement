@echo off
setlocal EnableExtensions DisableDelayedExpansion

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0scripts\release\Push-Releases.ps1" %*
exit /b %ERRORLEVEL%
