[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$output = Join-Path $PSScriptRoot ('..\artifacts\winui-smoke\' + [Guid]::NewGuid().ToString('N'))
$output = [IO.Path]::GetFullPath($output)
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Use an isolated, offline library: startup checks must never change the user's data.
@{ GamePath = (Join-Path $output 'NoGame'); Theme = 'Light'; WindowWidth = 1100; WindowHeight = 700 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'settings.json') -Encoding utf8
$owned = $null
try {
    $owned = Start-Process -FilePath $exe -ArgumentList @('--data', ('"' + $output + '"')) -PassThru -WindowStyle Hidden
    Write-Output "Owned smoke process PID: $($owned.Id)"
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 150
        $owned.Refresh()
        if ($owned.HasExited) { throw "WinUI startup exited early: $($owned.ExitCode)" }
    } while ((!$owned.MainWindowHandle -or $owned.MainWindowTitle -ne 'HS2 Manager') -and [DateTime]::UtcNow -lt $deadline)
    if (!$owned.MainWindowHandle -or $owned.MainWindowTitle -ne 'HS2 Manager') { throw 'WinUI main window did not appear.' }
    if (!(Test-Path -LiteralPath (Join-Path $output 'library.db'))) { throw 'Startup did not create the isolated library.' }
    Start-Sleep -Milliseconds 750
    if (!$owned.CloseMainWindow()) { throw 'Native close request failed.' }
    if (!$owned.WaitForExit(30000)) { throw 'WinUI process did not exit after closing.' }
    if ($owned.ExitCode -ne 0) { throw "WinUI shutdown failed: $($owned.ExitCode)" }
    Write-Output 'PASS published WinUI startup, isolated database and native close'
}
finally {
    if ($owned) {
        $owned.Refresh()
        if (!$owned.HasExited) { $owned.Kill($true); $owned.WaitForExit() }
        $owned.Dispose()
    }
}
