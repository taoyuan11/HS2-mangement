[CmdletBinding()]
param([switch]$RunPending)

function Get-DesktopCleanupStateDirectory {
    return Join-Path $env:LOCALAPPDATA 'HS2Manager/DesktopBuildCleanup'
}

function Get-DesktopCleanupDesktop {
    return [IO.Path]::GetFullPath([Environment]::GetFolderPath('DesktopDirectory')).TrimEnd('\', '/')
}

function Assert-DesktopCleanupStateDirectory {
    $state = Get-DesktopCleanupStateDirectory
    foreach ($path in @((Split-Path -Parent $state), $state)) {
        if (!(Test-Path -LiteralPath $path)) {
            New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
        }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "待清理记录目录不能是链接或文件：$path"
        }
    }
    return $state
}

function Set-DesktopCleanupRunOnce([string]$Runner) {
    $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $command = '"' + $powershell + '" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $Runner + '" -RunPending'
    if ($command.Length -gt 260) {
        throw '登录清理命令超过 Windows RunOnce 的 260 字符限制。请在关闭占用程序后运行 build-desktop.ps1 -CleanupOnly。'
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\RunOnce')
    try {
        $key.SetValue('!HS2Manager.DesktopBuildCleanup', $command, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Clear-DesktopCleanupRunOnce {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\RunOnce', $true)
    if ($key) {
        try {
            $key.DeleteValue('!HS2Manager.DesktopBuildCleanup', $false)
        }
        finally {
            $key.Dispose()
        }
    }
}

function Register-DesktopBuildCleanup([string]$Path) {
    $desktop = Get-DesktopCleanupDesktop
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $name = [IO.Path]::GetFileName($resolved)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $desktop -or $name -cnotmatch '^HS2Manager-backup-[0-9a-f]{32}$') {
        throw "拒绝登记桌面旧程序目录之外的清理目标：$resolved"
    }
    $state = Assert-DesktopCleanupStateDirectory
    # 只登记已完成替换的旧程序目录，不扫描通配符备份，更不登记回滚失败目录。
    $record = Join-Path $state ($name + '.json')
    if ((Test-Path -LiteralPath $record) -and ((Get-Item -LiteralPath $record -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "待清理记录不能是链接：$record"
    }
    @{Desktop = $desktop; Name = $name } | ConvertTo-Json | Set-Content -LiteralPath $record -Encoding UTF8 -ErrorAction Stop
    foreach ($file in @('DesktopBuildCleanup.ps1', 'DesktopBuildCleanup.cs')) {
        $target = Join-Path $state $file
        if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "登录清理文件不能是链接：$target"
        }
        $source = Join-Path $PSScriptRoot $file
        if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)) {
            [IO.File]::Copy($source, $target, $true)
        }
    }
    Set-DesktopCleanupRunOnce (Join-Path $state 'DesktopBuildCleanup.ps1')
}

function Invoke-PendingDesktopBuildCleanup([switch]$RegisterForLogon) {
    $state = Get-DesktopCleanupStateDirectory
    if (!(Test-Path -LiteralPath $state)) {
        return
    }
    $state = Assert-DesktopCleanupStateDirectory
    $desktop = Get-DesktopCleanupDesktop
    if (!('HS2Manager.Build.DesktopBuildCleanup' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'DesktopBuildCleanup.cs') -ErrorAction Stop
    }
    foreach ($file in Get-ChildItem -LiteralPath $state -Filter 'HS2Manager-backup-*.json' -Force -ErrorAction Stop) {
        try {
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "待清理记录必须是普通文件：$($file.FullName)"
            }
            $record = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            if ($record.Desktop -ne $desktop -or $record.Name -cnotmatch '^HS2Manager-backup-[0-9a-f]{32}$' -or
                $file.Name -cne ($record.Name + '.json')) {
                throw "待清理记录与当前桌面目录不匹配：$($file.FullName)"
            }
            $path = Join-Path $desktop $record.Name
            [HS2Manager.Build.DesktopBuildCleanup]::Remove($path, $desktop, $record.Name)
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
            Write-Output "已清理旧程序目录：$path"
        }
        catch {
            $detail = $_.Exception
            while ($detail.InnerException) {
                $detail = $detail.InnerException
            }
            Write-Warning "旧程序清理仍待重试：$($detail.Message)"
        }
    }
    if (!(Get-ChildItem -LiteralPath $state -Filter 'HS2Manager-backup-*.json' -Force -ErrorAction Stop)) {
        Clear-DesktopCleanupRunOnce
    }
    elseif ($RegisterForLogon) {
        Set-DesktopCleanupRunOnce (Join-Path $state 'DesktopBuildCleanup.ps1')
    }
}

if ($RunPending) {
    $ErrorActionPreference = 'Stop'
    Invoke-PendingDesktopBuildCleanup
}
