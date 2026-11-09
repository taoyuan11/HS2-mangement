param([Parameter(Mandatory=$true)][string]$Package)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $workspace 'scripts\LocalWorkspace.ps1')
if (![HS2Manager.Core.StoragePaths]::IsNetwork($workspace)) { throw 'This check requires a NAS workspace.' }
$fixture = Join-Path $workspace ('artifacts\nas-portable-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $fixture 'portable app'
New-Item -ItemType Directory -Path $install -Force | Out-Null
Get-ChildItem -LiteralPath $Package -Force | Where-Object Name -ne 'Data' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse
}
$oldData = Join-Path $install 'Data'
New-Item -ItemType Directory -Path $oldData -Force | Out-Null
@{ GamePath = (Join-Path $fixture 'NoGame'); Theme = 'Dark' } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $oldData 'settings.json') -Encoding utf8
$originalSettings = (Get-FileHash -LiteralPath (Join-Path $oldData 'settings.json')).Hash
$localData = Join-Path (Get-Hs2LocalCache $install 'Portable') 'Data'
if (Test-Path -LiteralPath $localData) { throw 'The isolated test library already exists.' }
$previousSource = $env:HS2MANAGER_SOURCE_ROOT
$previousOutput = $env:HS2MANAGER_SOURCE_OUTPUT
$owned = $null
try {
    $env:HS2MANAGER_SOURCE_ROOT = $null
    $env:HS2MANAGER_SOURCE_OUTPUT = $null
    foreach ($path in @($install, [HS2Manager.Core.StoragePaths]::Identity($install))) {
        $owned = Start-Process -FilePath (Join-Path $path 'HS2Manager.exe') -WorkingDirectory $path -PassThru -WindowStyle Hidden
        Write-Output "Owned NAS portable PID: $($owned.Id)"
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            Start-Sleep -Milliseconds 150
            $owned.Refresh()
            if ($owned.HasExited) { throw "NAS portable exited early: $($owned.ExitCode)" }
        } while ($owned.MainWindowTitle -ne 'HS2 Manager' -and [DateTime]::UtcNow -lt $deadline)
        if ($owned.MainWindowTitle -ne 'HS2 Manager') { throw 'NAS portable did not open its main window.' }
        if (!(Test-Path -LiteralPath (Join-Path $localData 'library.db'))) { throw 'Default NAS startup did not create a local database.' }
        if (Test-Path -LiteralPath (Join-Path $oldData 'library.db')) { throw 'A database was written to the NAS installation.' }
        if (!$owned.CloseMainWindow() -or !$owned.WaitForExit(20000) -or $owned.ExitCode -ne 0) { throw 'NAS portable did not close cleanly.' }
        $owned.Dispose(); $owned = $null
        $selected = Join-Path (Get-Hs2LocalCache $path 'Portable') 'Data'
        if ($selected -ne $localData) { throw 'Mapped drive and UNC launch selected different libraries.' }
        if ((Get-Content -LiteralPath (Join-Path $localData 'settings.json') -Raw | ConvertFrom-Json).Theme -ne 'Dark') { throw 'NAS settings migration lost the theme.' }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $oldData 'settings.json')).Hash -ne $originalSettings) { throw 'Migration modified the original NAS settings.' }
    Write-Output 'PASS NAS portable default launch via mapped drive and UNC, local library migration, original data preservation and normal shutdown'
    Write-Output "Local test library: $localData"
}
finally {
    if ($owned) { if (!$owned.HasExited) { $owned.Kill($true); $owned.WaitForExit() }; $owned.Dispose() }
    $env:HS2MANAGER_SOURCE_ROOT = $previousSource
    $env:HS2MANAGER_SOURCE_OUTPUT = $previousOutput
}
