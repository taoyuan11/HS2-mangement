[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { throw '需要 .NET 10 SDK。请运行 bootstrap-dotnet.ps1，或安装官方 .NET 10 SDK。' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码 $LASTEXITCODE" }
}
if ($PreviewTest -and !(Test-Path -LiteralPath (Join-Path $GamePath 'abdata\chara\oo_base.unity3d'))) { throw "游戏资源目录无效：$GamePath" }
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$packageName = 'HS2Manager-1.1.0-preview2-win-x64'
$package = Join-Path $releaseRoot $packageName
$staging = Join-Path $releaseRoot ($packageName + '-staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    if (!$SkipTests) {
        Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.Checks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--core', '--game', $GamePath)
        Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.UiChecks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false')
    }
    if ($PreviewTest) {
        $sampleArguments = if ($PreviewCard) { @('--card', $PreviewCard) } else { @() }
        Invoke-Dotnet (@('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.Checks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--native-preview', '--game', $GamePath) + $sampleArguments)
        Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.UiChecks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--native-preview', '--game', $GamePath)
        if ($PreviewCard) { Invoke-Dotnet (@('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.UiChecks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--native-preview', '--game', $GamePath) + $sampleArguments) }
    }
    Invoke-Dotnet @('publish', (Join-Path $projectRoot 'src\HS2Manager.App'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseSharedCompilation=false', '--disable-build-servers', '-o', $staging, '--nologo')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $staging '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $staging -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $staging -Recurse
    New-Item -ItemType Directory -Path (Join-Path $staging 'Data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $staging 'Data\README.txt') -Value '首次启动会在这里创建本地数据库、设置、封面缓存和日志。移动便携包时请保留整个 Data 文件夹。' -Encoding utf8
    # Never overwrite a working portable installation or its Data directory.
    if (Test-Path -LiteralPath $package) { $package = Join-Path $releaseRoot ($packageName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedPackage = [IO.Path]::GetFullPath($package)
    $releasePrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
    if (!$resolvedStaging.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or !$resolvedPackage.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录超出工作区。' }
    Move-Item -LiteralPath $resolvedStaging -Destination $resolvedPackage
    $zip = $resolvedPackage + '.zip'
    Compress-Archive -LiteralPath $resolvedPackage -DestinationPath $zip -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    Set-Content -LiteralPath ($zip + '.sha256') -Value ($hash.Hash + '  ' + [IO.Path]::GetFileName($zip)) -Encoding ascii
    Write-Output "便携包：$zip"
    Write-Output "SHA-256：$($hash.Hash)"
}
finally {
    & $dotnet build-server shutdown | Out-Null
}
