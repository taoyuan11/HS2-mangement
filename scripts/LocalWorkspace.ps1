# Shared with the application's data-location logic. Supports Windows PowerShell 5.1.
if (!('HS2Manager.Core.StoragePaths' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '..\src\HS2Manager.Core\StoragePaths.cs')
}

function Get-Hs2LocalCache([string]$Path, [string]$Category = 'Source') {
    [HS2Manager.Core.StoragePaths]::LocalCache($Path, $Category, [Environment]::GetFolderPath('LocalApplicationData'))
}

function Get-Hs2BuildArguments([string]$Workspace, [string]$Configuration) {
    if ([HS2Manager.Core.StoragePaths]::IsNetwork($Workspace)) {
        $localRoot = Get-Hs2LocalCache (Join-Path $Workspace ('src\HS2Manager.App\bin\' + $Configuration))
        @('--artifacts-path', (Join-Path $localRoot 'Build'))
    }
}

function Sync-Hs2UnityProject([string]$Source, [string]$Cache) {
    $target = [IO.Path]::GetFullPath((Join-Path $Cache 'Project'))
    $allowed = [IO.Path]::GetFullPath($Cache).TrimEnd('\') + '\'
    if (!$target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
        [HS2Manager.Core.StoragePaths]::IsNetwork($target)) { throw 'Unity build cache must be inside the local cache directory.' }
    foreach ($name in @('Assets', 'Packages', 'ProjectSettings')) {
        $from = Join-Path $Source $name
        $to = Join-Path $target $name
        if (!(Test-Path -LiteralPath $from -PathType Container)) { throw "Unity source directory is missing: $from" }
        foreach ($directory in @($Cache, $target, $to)) {
            if ((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unity cache must not contain directory links: $directory"
            }
        }
        # Mirror only these generated local copies. Never mirror Library/Temp or
        # remove files from the source checkout. /XJ avoids following junctions.
        & robocopy.exe $from $to /MIR /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Host
        if ($LASTEXITCODE -ge 8) { throw "Could not copy Unity sources to local disk: $from (robocopy $LASTEXITCODE)" }
    }
    $target
}
