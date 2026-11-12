[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$FixtureTool
)
$ErrorActionPreference = 'Stop'
$sourceExe = (Resolve-Path -LiteralPath $Executable).Path
$fixture = (Resolve-Path -LiteralPath $FixtureTool).Path
$data = Join-Path $env:LOCALAPPDATA ('HS2Manager/Tests/cpp-winui/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data -Force | Out-Null
$package = Join-Path $data 'Package'
New-Item -ItemType Directory -Path $package | Out-Null
# 使用独立程序副本；最后必须能完整删除运行库，才能证明没有进程/崩溃快照占用。
Get-ChildItem -LiteralPath (Split-Path -Parent $sourceExe) -Force | Where-Object { $_.Name -notmatch '^Data(?:\.|$)' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $package -Recurse
}
$exe = Join-Path $package ([IO.Path]::GetFileName($sourceExe))
@{ GamePath = (Join-Path $data 'NoGame')
    Theme = 'Light'
    WindowWidth = 1100
    WindowHeight = 700
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8

function Test-WindowStartup([string]$DataDirectory, [string]$Label, [switch]$ForceExit, [int]$SettleMilliseconds = 2500) {
    $owned = $null
    $ownedId = 0
    try {
        # 显示真实窗口，使首次布局及封面加载参与检查。
        $owned = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList @('--data', ('"' + $DataDirectory + '"')) -PassThru
        $ownedId = $owned.Id
        Write-Output "Owned C++ smoke PID: $($owned.Id)"
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 150
            $owned.Refresh()
            if ($owned.HasExited) {
                $log = Join-Path $DataDirectory 'native-error.log'
                if (Test-Path -LiteralPath $log) {
                    Get-Content -LiteralPath $log | Write-Output
                }
                throw "C++ WinUI 启动失败：$($owned.ExitCode)"
            }
        } while ((!$owned.MainWindowHandle -or $owned.MainWindowTitle -ne 'HS2 Manager') -and [DateTime]::UtcNow -lt $deadline)
        if (!$owned.MainWindowHandle -or $owned.MainWindowTitle -ne 'HS2 Manager') {
            throw '未发现 WinUI 主窗口。'
        }
        if (!(Test-Path -LiteralPath (Join-Path $DataDirectory 'library.db'))) {
            throw '未创建独立测试资料库。'
        }
        $managed = @($owned.Modules | Where-Object { $_.ModuleName -match '^(coreclr|clr|hostfxr|hostpolicy|mscoree|Unity|mono-2\.0)' })
        if ($managed.Count) {
            throw ('加载了非原生运行时：' + ($managed.ModuleName -join ', '))
        }
        # 覆盖异步封面加载、首次扫描及结果刷新，不能只检查窗口刚创建的瞬间。
        if ($owned.WaitForExit($SettleMilliseconds)) {
            throw "WinUI 异步启动失败（$Label）：$($owned.ExitCode)"
        }
        if ($ForceExit) {
            $owned.Kill()
        }
        elseif (!$owned.CloseMainWindow()) {
            throw '无法请求主窗口关闭。'
        }
        if (!$owned.WaitForExit(30000)) {
            throw 'WinUI 关闭时未完成任务清理。'
        }
        if (!$ForceExit -and $owned.ExitCode -ne 0) {
            throw "WinUI 关闭异常：$($owned.ExitCode)"
        }
        $log = Join-Path $DataDirectory 'native-error.log'
        if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 0) {
            throw ("WinUI 启动或退出出现异常（$Label）：" + (Get-Content -LiteralPath $log -Raw))
        }
        Write-Output "PASS C++ WinUI $Label, isolated SQLite, no CLR modules, process exit $($owned.ExitCode)"
    }
    finally {
        if ($owned) {
            $owned.Refresh()
            if (!$owned.HasExited) {
                $owned.Kill()
                $owned.WaitForExit()
            }
            $owned.Dispose()
        }
    }
    $remaining = @(Get-CimInstance Win32_Process -Filter "ProcessId=$ownedId OR ParentProcessId=$ownedId")
    if ($remaining.Count) {
        throw ("WinUI 退出后残留进程或崩溃快照：" + ($remaining.ProcessId -join ', '))
    }
}

try {
    Test-WindowStartup $data 'empty library'
    Test-WindowStartup $data 'forced termination' -ForceExit
    Test-WindowStartup $data 'restart after forced termination'
    $fixtureRoot = Join-Path $data 'Fixture'
    & $fixture $fixtureRoot (Join-Path (Split-Path -Parent $exe) 'Assets/Brand/hs2-manager.png')
    if ($LASTEXITCODE -ne 0) {
        throw '无法生成独立 WinUI 人物库。'
    }
    $populatedData = Join-Path $fixtureRoot 'Data'
    # 首次索引从空数据库开始，再验证已有资料库和第二次启动。
    $scanData = Join-Path $data 'FirstScan'
    New-Item -ItemType Directory -Path $scanData | Out-Null
    Copy-Item -LiteralPath (Join-Path $populatedData 'settings.json') -Destination $scanData
    Test-WindowStartup $scanData 'close during first scan' -SettleMilliseconds 0
    Test-WindowStartup $scanData 'first card scan'
    & $fixture --verify $scanData
    if ($LASTEXITCODE -ne 0) {
        throw 'WinUI 首次扫描结果不完整。'
    }
    foreach ($attempt in 1..2) {
        Test-WindowStartup $populatedData "populated library restart $attempt"
        & $fixture --verify $populatedData
        if ($LASTEXITCODE -ne 0) {
            throw 'WinUI 重启后人物库不完整。'
        }
    }
}
finally {
    $resolvedPackage = [IO.Path]::GetFullPath($package)
    if ([IO.Path]::GetDirectoryName($resolvedPackage) -ne [IO.Path]::GetFullPath($data) -or
        [IO.Path]::GetFileName($resolvedPackage) -ne 'Package') {
        throw 'WinUI 测试程序清理路径越界。'
    }
    if (Test-Path -LiteralPath $resolvedPackage) {
        Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
    }
}
Write-Output 'PASS deleted the complete executable/runtime directory after all WinUI exit checks'
