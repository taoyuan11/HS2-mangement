[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$restore = Join-Path $repository 'scripts/build/Restore-NativeDependencies.ps1'
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $temporary ('hs2-sdk-check-' + [Guid]::NewGuid().ToString('N'))

function Assert-Failure([scriptblock]$Action, [string]$Message) {
    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -like $Message) {
            return
        }
        throw
    }
    throw "Expected failure: $Message"
}

# 只注入下载失败；不访问网络或运行安装程序，覆盖真实的暂存及校验路径。
function Invoke-WebRequest {
    param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing)
    [IO.File]::WriteAllText($OutFile, 'incomplete download')
    throw 'Simulated interrupted download'
}

function Get-Command {
    param([string]$Name, [string]$ErrorAction)
    if ($Name -eq '7z.exe') {
        # 失败发生在解压之前，只需提供存在的文件，使检查不依赖本机 7-Zip。
        return [pscustomobject]@{Source = (Get-Process -Id $PID).Path }
    }
    Microsoft.PowerShell.Core\Get-Command $Name -ErrorAction $ErrorAction
}

try {
    $interrupted = Join-Path $root 'interrupted'
    Assert-Failure { & $restore -Directory $interrupted -Dependency Fbx } '*Simulated interrupted download*'
    if (@(Get-ChildItem -LiteralPath $interrupted -Force).Count) {
        throw 'Interrupted download left a cached installer or staging directory.'
    }
    # 同一目录重试仍应进入下载，不能将半成品当成已完成的缓存。
    Assert-Failure { & $restore -Directory $interrupted -Dependency Fbx } '*Simulated interrupted download*'
    if (@(Get-ChildItem -LiteralPath $interrupted -Force).Count) {
        throw 'Retry left partial dependency files.'
    }
    Write-Output 'PASS interrupted download cleanup and retry'

    $corrupt = Join-Path $root 'corrupt'
    New-Item -ItemType Directory -Path $corrupt -Force | Out-Null
    $installer = Join-Path $corrupt 'fbx202037_fbxsdk_vs2022_win.exe'
    [IO.File]::WriteAllText($installer, 'invalid installer')
    Assert-Failure { & $restore -Directory $corrupt -Dependency Fbx } '*FBX SDK 下载校验失败*'
    if (@(Get-ChildItem -LiteralPath $corrupt -Force).Count -ne 1 -or
        [IO.File]::ReadAllText($installer) -ne 'invalid installer') {
        throw 'Checksum failure changed the existing input or published an SDK.'
    }
    Write-Output 'PASS corrupt installer rejected before extraction'

    $incomplete = Join-Path $root 'incomplete'
    $physx = Join-Path $incomplete 'physx-3.4'
    New-Item -ItemType Directory -Path $physx -Force | Out-Null
    $localFile = Join-Path $physx 'local-work.txt'
    [IO.File]::WriteAllText($localFile, 'preserve this file')
    Assert-Failure { & $restore -Directory $incomplete -Dependency PhysX } '*PhysX 缓存不完整*'
    if ([IO.File]::ReadAllText($localFile) -ne 'preserve this file' -or
        @(Get-ChildItem -LiteralPath $incomplete -Force).Count -ne 1) {
        throw 'Incomplete PhysX cache was overwritten.'
    }
    Write-Output 'PASS incomplete PhysX cache rejected without overwriting local files'

    $missing = Join-Path $root 'missing-explicit-sdk'
    Assert-Failure { & (Join-Path $repository 'scripts/build/Build-NativePhysics.ps1') -Source $missing } "*missing or incomplete: $missing.*"
    Assert-Failure { & (Join-Path $repository 'scripts/build/Build-NativeFbx.ps1') -SdkRoot $missing } "*missing or incomplete: $missing.*"
    if (Test-Path -LiteralPath $missing) {
        throw 'Explicit SDK path was overwritten.'
    }
    Write-Output 'PASS explicit SDK paths fail without automatic replacement'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporary -or
        ![IO.Path]::GetFileName($resolved).StartsWith('hs2-sdk-check-')) {
        throw 'SDK 测试目录越界。'
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
