[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Package, [Parameter(Mandatory = $true)][string]$Checker)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Package).Path)
$testParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'HS2Manager/Tests'))
$root = Join-Path $testParent ('update-process-' + [Guid]::NewGuid().ToString('N'))
$installation = Join-Path $root 'Old installation'
$data = Join-Path $installation 'Data'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$helper = $null
$cache = ''
try {
    New-Item -ItemType Directory -Path $installation -Force | Out-Null
    & robocopy.exe $source $installation /E /MT:4 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw '无法准备隔离安装副本。'
    }
    $info = Join-Path $installation 'package-info.json'
    $metadata = Get-Content -LiteralPath $info -Raw | ConvertFrom-Json
    $metadata.version = '1.4.9'
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $info -Encoding utf8
    $manifestPath = Join-Path $installation 'package-files.json'
    $manifest = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
    foreach ($entry in $manifest) {
        if ($entry.Path -eq 'package-info.json') {
            $entry.Bytes = (Get-Item -LiteralPath $info).Length
            $entry.SHA256 = (Get-FileHash -LiteralPath $info).Hash
        }
    }
    $oldOnly = Join-Path $installation 'obsolete-test.txt'
    'obsolete' | Set-Content -LiteralPath $oldOnly -Encoding utf8
    $manifest += @{ Path = 'obsolete-test.txt'
        Bytes = (Get-Item -LiteralPath $oldOnly).Length
        SHA256 = (Get-FileHash -LiteralPath $oldOnly).Hash
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    @{ GamePath = (Join-Path $root 'NoGame')
        AutoCheckUpdates = $false
        Theme = 'Light'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
    'keep user file' | Set-Content -LiteralPath (Join-Path $installation 'agent.json') -Encoding utf8
    $before = (Get-FileHash -LiteralPath (Join-Path $installation 'agent.json')).Hash
    $handoff = @(& $Checker --handoff $installation $source $data)
    if ($LASTEXITCODE -ne 0) {
        throw ('更新进程交接失败：' + ($handoff -join ', '))
    }
    $helperLine = $handoff | Where-Object { $_ -like 'HELPER:*' } | Select-Object -First 1
    $helperId = [int]$helperLine.Substring(7)
    $cache = ($handoff | Where-Object { $_ -like 'CACHE:*' } | Select-Object -First 1).Substring(6)
    $helper = Get-Process -Id $helperId -ErrorAction SilentlyContinue
    if ($helper) {
        if ($helper.Path -ne (Join-Path $cache 'HS2Updater.exe')) {
            throw '助手进程标识不符。'
        }
        $owned.Add($helper)
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $app = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$helperId")
        foreach ($child in $children) {
            if ($child.ExecutablePath -eq (Join-Path $installation 'HS2Manager.exe')) {
                $app = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                if ($app) {
                    $owned.Add($app)
                    break
                }
            }
        }
        if ($app) {
            break
        }
        if ($helper -and $helper.HasExited) {
            break
        }
        Start-Sleep -Milliseconds 200
    }
    if (!$app) {
        throw "更新后未找到新程序。诊断目录：$cache"
    }
    if ($helper -and !$helper.WaitForExit(60000)) {
        throw "更新助手没有退出。诊断目录：$cache"
    }
    $journal = Get-Content -LiteralPath (Join-Path $cache 'transaction.json') -Raw | ConvertFrom-Json
    if ($journal.state -ne 'complete') {
        throw '新版启动后未确认完成更新。'
    }
    $app.Refresh()
    if ($app.HasExited -or !$app.MainWindowHandle) {
        throw '新版 WinUI 未保持正常运行。'
    }
    # 提前持有进程句柄，避免按 PID 获取的对象在快速退出后无法读取退出码。
    $processHandle = $app.Handle
    $closeAccepted = $app.CloseMainWindow()
    $exited = $app.WaitForExit(30000)
    $exitCode = if ($exited) {
        $app.ExitCode
    }
    else {
        $null
    }
    if (!$closeAccepted -or !$exited -or $exitCode -ne 0) {
        $startupLog = Join-Path $data 'native-error.log'
        if (Test-Path -LiteralPath $startupLog) {
            Get-Content -LiteralPath $startupLog
        }
        throw "新版没有正常关闭：accepted=$closeAccepted exited=$exited code=$exitCode"
    }
    if (!(Test-Path -LiteralPath (Join-Path $data 'library.db')) -or
        (Get-FileHash -LiteralPath (Join-Path $installation 'agent.json')).Hash -ne $before -or
        (Test-Path -LiteralPath $oldOnly)) {
        throw '数据保留或旧文件清理不符。'
    }
    $newInfo = Get-Content -LiteralPath $info -Raw | ConvertFrom-Json
    $expected = Get-Content -LiteralPath (Join-Path $source 'package-info.json') -Raw | ConvertFrom-Json
    if ($newInfo.version -ne $expected.version) {
        throw '安装后版本不符。'
    }
    Write-Output 'PASS updater handoff, parent exit, replacement, new WinUI startup acknowledgement, data preservation and process cleanup.'
}
finally {
    # 即使助手在交接后失败，也回收本次唯一安装副本下已启动的新窗口。
    foreach ($child in @(Get-CimInstance Win32_Process -Filter "Name='HS2Manager.exe'")) {
        if ($child.ExecutablePath -eq (Join-Path $installation 'HS2Manager.exe') -and
            !($owned | Where-Object { $_.Id -eq $child.ProcessId })) {
            $remaining = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if ($remaining) {
                $owned.Add($remaining)
            }
        }
    }
    foreach ($process in $owned) {
        try {
            if (!$process.HasExited) {
                $process.Kill()
                $process.WaitForExit(10000) | Out-Null
            }
        }
        finally {
            $process.Dispose()
        }
    }
    # 仅清理本次创建且已确认位于测试父目录内的副本。
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($root)) -ne $testParent -or
        ![IO.Path]::GetFileName($root).StartsWith('update-process-')) {
        throw '测试清理路径越界。'
    }
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if ($cache) {
        $allowed = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'HS2Manager/Updates'))
        $resolved = [IO.Path]::GetFullPath($cache)
        if ([IO.Path]::GetDirectoryName($resolved) -ne $allowed -or [IO.Path]::GetFileName($resolved) -notmatch '^[0-9a-f]{24}$') {
            throw '更新测试缓存路径越界。'
        }
        if (Test-Path -LiteralPath $resolved) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
