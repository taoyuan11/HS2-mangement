[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$data = Join-Path $env:LOCALAPPDATA ('HS2Manager/Tests/cpp-winui/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data -Force | Out-Null
@{ GamePath = (Join-Path $data 'NoGame'); Theme='Light'; WindowWidth=1100; WindowHeight=700 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
$owned=$null
try {
    $owned=Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList @('--data',('"'+$data+'"')) -WindowStyle Hidden -PassThru
    Write-Output "Owned C++ smoke PID: $($owned.Id)"
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 150
        $owned.Refresh()
        if ($owned.HasExited) {
            $log=Join-Path $data 'native-error.log'
            if(Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | Write-Output }
            throw "C++ WinUI 启动失败：$($owned.ExitCode)"
        }
    } while ((!$owned.MainWindowHandle -or $owned.MainWindowTitle -ne 'HS2 Manager') -and [DateTime]::UtcNow -lt $deadline)
    if (!$owned.MainWindowHandle -or $owned.MainWindowTitle -ne 'HS2 Manager') { throw '未发现 WinUI 主窗口。' }
    if (!(Test-Path -LiteralPath (Join-Path $data 'library.db'))) { throw '未创建独立测试资料库。' }
    $managed=@($owned.Modules|Where-Object {$_.ModuleName -match '^(coreclr|clr|hostfxr|hostpolicy|mscoree|Unity|mono-2\.0)'})
    if ($managed.Count) { throw ('加载了非原生运行时：'+($managed.ModuleName -join ', ')) }
    Start-Sleep -Milliseconds 600
    if (!$owned.CloseMainWindow()) { throw '无法请求主窗口关闭。' }
    if (!$owned.WaitForExit(30000)) { throw 'WinUI 关闭时未完成任务清理。' }
    if ($owned.ExitCode -ne 0) { throw "WinUI 关闭异常：$($owned.ExitCode)" }
    Write-Output 'PASS C++ WinUI startup, isolated SQLite, no CLR modules, native window close'
} finally {
    if ($owned) {
        $owned.Refresh()
        if (!$owned.HasExited) { $owned.Kill(); $owned.WaitForExit() }
        $owned.Dispose()
    }
}
