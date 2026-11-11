@echo off
setlocal EnableExtensions DisableDelayedExpansion

pushd "%~dp0"
if errorlevel 1 goto directory_error

set "MSBUILDDISABLENODEREUSE=1"
echo Building and starting HS2 Manager (C++ / WinUI 3)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\development\Start-CppSource.ps1" %*
set "HS2_EXIT_CODE=%ERRORLEVEL%"
popd
if not "%HS2_EXIT_CODE%"=="0" (
    echo.
    echo HS2 Manager failed. See the error above.
    pause
)
exit /b %HS2_EXIT_CODE%

:directory_error
echo Cannot open the project directory: "%~dp0"
pause
exit /b 1
