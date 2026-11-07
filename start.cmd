@echo off
setlocal EnableExtensions DisableDelayedExpansion

pushd "%~dp0"
if errorlevel 1 goto directory_error

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "MSBUILDDISABLENODEREUSE=1"
set "HS2_DOTNET=%~dp0.tools\dotnet\dotnet.exe"
if exist "%HS2_DOTNET%" goto launch

where.exe dotnet >nul 2>&1
if errorlevel 1 goto missing_sdk
set "HS2_DOTNET=dotnet"

:launch
echo Building and starting HS2 Manager...
"%HS2_DOTNET%" run --project "src\HS2Manager.App\HS2Manager.App.csproj" --configuration Release --no-launch-profile --disable-build-servers -p:UseSharedCompilation=false -- %*
set "HS2_EXIT_CODE=%ERRORLEVEL%"
popd
if not "%HS2_EXIT_CODE%"=="0" (
    echo.
    echo HS2 Manager failed. See the error above.
    pause
)
exit /b %HS2_EXIT_CODE%

:missing_sdk
echo .NET 10 SDK was not found. Install it or run:
echo powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bootstrap-dotnet.ps1"
popd
pause
exit /b 1

:directory_error
echo Cannot open the project directory: "%~dp0"
pause
exit /b 1
