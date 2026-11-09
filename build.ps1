[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard = '',
    [string]$UnityPlayerDirectory = '',
    [string]$OutputDirectory = '',
    [switch]$SkipArchive
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-Path $projectRoot 'scripts\LocalWorkspace.ps1')
$env:HS2MANAGER_SOURCE_ROOT = $projectRoot
$testRoot = if ([HS2Manager.Core.StoragePaths]::IsNetwork($projectRoot)) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HS2Manager\Tests' } else { $projectRoot }
$env:HS2MANAGER_TEST_ROOT = $testRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { throw '需要 .NET 10 SDK。请运行 bootstrap-dotnet.ps1，或安装官方 .NET 10 SDK。' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
function Invoke-Dotnet([string[]]$Arguments) {
    # Prevent services from starting; do not shut down build servers owned by other sessions.
    $Arguments = @($Arguments[0], '--disable-build-servers') + $Arguments[1..($Arguments.Length - 1)]
    $configurationIndex = [Array]::IndexOf($Arguments, '-c')
    $configuration = if ($configurationIndex -ge 0) { $Arguments[$configurationIndex + 1] } else { 'Release' }
    $buildArguments = @(Get-Hs2BuildArguments $projectRoot $configuration)
    $Arguments = @($Arguments[0]) + $buildArguments + $Arguments[1..($Arguments.Length - 1)]
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码 $LASTEXITCODE" }
}
if ($PreviewTest -and !(Test-Path -LiteralPath (Join-Path $GamePath 'abdata\chara\oo_base.unity3d'))) { throw "游戏资源目录无效：$GamePath" }
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$packageName = 'HS2Manager-1.2.0-preview2-winui3-win-x64'
if ($OutputDirectory) {
    $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    $OutputDirectory = $OutputDirectory.TrimEnd('\', '/')
    $releaseRoot = Split-Path -Parent $OutputDirectory
    $packageName = Split-Path -Leaf $OutputDirectory
    if (!$releaseRoot -or !$packageName) { throw '输出目录必须是一个子文件夹，不能是磁盘根目录。' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw "输出目录已存在，请先移走或重命名后重试：$OutputDirectory" }
}
if(!$UnityPlayerDirectory){$UnityPlayerDirectory=Join-Path $projectRoot ('artifacts\unity-preview\release-'+[Guid]::NewGuid().ToString('N')); & (Join-Path $projectRoot 'build-unity-preview.ps1') -OutputPath $UnityPlayerDirectory}
$UnityPlayerDirectory=(Resolve-Path -LiteralPath $UnityPlayerDirectory).Path
foreach($required in @('HS2Manager.UnityPreview.exe','UnityPlayer.dll','HS2Manager.UnityPreview_Data','MonoBleedingEdge')){if(!(Test-Path -LiteralPath (Join-Path $UnityPlayerDirectory $required))){throw "独立 Unity 发布文件缺失：$required"}}
$package = Join-Path $releaseRoot $packageName
$stagingRoot = if ([HS2Manager.Core.StoragePaths]::IsNetwork($projectRoot)) { Join-Path (Get-Hs2LocalCache $projectRoot 'Publish') 'Staging' } else { $releaseRoot }
$staging = Join-Path $stagingRoot ($packageName + '-staging-' + [Guid]::NewGuid().ToString('N'))
$delivery = Join-Path $releaseRoot ($packageName + '-staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    if (!$SkipTests) {
        Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.Checks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--core', '--game', $GamePath)
        Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.UiChecks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false')
    }
    if ($PreviewTest) {
        $sampleArguments = if ($PreviewCard) { @('--card', $PreviewCard) } else { @() }
        $player=Join-Path $UnityPlayerDirectory 'HS2Manager.UnityPreview.exe'
        Invoke-Dotnet (@('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.Checks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--prepare-unity-character', '--game', $GamePath) + $sampleArguments)
        Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.Checks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--unity-session', '--player', $player, '--definition', (Join-Path $testRoot 'artifacts\checks\unity-character.json'))
        Invoke-Dotnet (@('run', '--project', (Join-Path $projectRoot 'tests\HS2Manager.UiChecks'), '-c', 'FinalValidation', '-p:UseSharedCompilation=false', '--', '--unity-preview', '--game', $GamePath, '--player', $player) + $sampleArguments)
    }
    Invoke-Dotnet @('publish', (Join-Path $projectRoot 'src\HS2Manager.App'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseSharedCompilation=false', '-o', $staging, '--nologo')
    if (!$SkipTests) { & (Join-Path $projectRoot 'tests\Invoke-WinUiSmoke.ps1') -Executable (Join-Path $staging 'HS2Manager.exe') }
    $unityTarget=Join-Path $staging 'UnityPreview';New-Item -ItemType Directory -Path $unityTarget|Out-Null
    foreach($name in @('HS2Manager.UnityPreview.exe','UnityPlayer.dll','HS2Manager.UnityPreview_Data','MonoBleedingEdge')){Copy-Item -LiteralPath (Join-Path $UnityPlayerDirectory $name) -Destination $unityTarget -Recurse}
    $forbidden=@(Get-ChildItem -LiteralPath $staging -Recurse -File|Where-Object {$_.Name -match '^(Assembly-CSharp|IL|BepInEx|HS2_|HS2Manager\.Bridge|HS2Manager\.GameReference)' -or $_.Extension -in '.zipmod','.unity3d'})
    if($forbidden.Count){throw ('发布包包含游戏、插件或测试资源：'+($forbidden.Name -join ', '))}
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $staging '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $staging -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $staging -Recurse
    New-Item -ItemType Directory -Path (Join-Path $staging 'Data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $staging 'Data\README.txt') -Value '本机运行时在这里创建数据库、设置、封面缓存和日志。NAS 运行时自动迁移至本机 LocalAppData\HS2Manager\Portable；实际位置见设置。请保留原 Data 文件夹。' -Encoding utf8
    # Never overwrite a working portable installation or its Data directory.
    if (Test-Path -LiteralPath $package) {
        if ($OutputDirectory) { throw "输出目录已存在，请先移走或重命名后重试：$package" }
        $package = Join-Path $releaseRoot ($packageName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedPackage = [IO.Path]::GetFullPath($package)
    $releasePrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
    $stagingPrefix = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\') + '\'
    if (!$resolvedStaging.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or !$resolvedPackage.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录超出指定目录。' }
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    if ([HS2Manager.Core.StoragePaths]::IsNetwork($projectRoot)) {
        # Move-Item cannot move directories across volumes. Copy to a fresh
        # destination staging directory, then publish with a same-directory rename.
        $resolvedDelivery = [IO.Path]::GetFullPath($delivery)
        if (!$resolvedDelivery.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '发布中转目录超出工作区。' }
        Copy-Item -LiteralPath $resolvedStaging -Destination $resolvedDelivery -Recurse
        Move-Item -LiteralPath $resolvedDelivery -Destination $resolvedPackage
    }
    else { Move-Item -LiteralPath $resolvedStaging -Destination $resolvedPackage }
    Write-Output "便携目录：$resolvedPackage"
    if (!$SkipArchive) {
        $zip = $resolvedPackage + '.zip'
        Compress-Archive -LiteralPath $resolvedPackage -DestinationPath $zip -CompressionLevel Optimal
        $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
        Set-Content -LiteralPath ($zip + '.sha256') -Value ($hash.Hash + '  ' + [IO.Path]::GetFileName($zip)) -Encoding ascii
        Write-Output "便携包：$zip"
        Write-Output "SHA-256：$($hash.Hash)"
    }
}
finally {
    if (Test-Path -LiteralPath $delivery) {
        $resolvedDelivery = [IO.Path]::GetFullPath($delivery)
        $releasePrefix = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
        if (!$resolvedDelivery.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedDelivery) -notlike ($packageName + '-staging-*')) { throw '发布中转目录超出工作区。' }
        Remove-Item -LiteralPath $resolvedDelivery -Recurse -Force
    }
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = [IO.Path]::GetFullPath($staging)
        $stagingPrefix = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\') + '\'
        if (!$resolvedStaging.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStaging) -notlike ($packageName + '-staging-*')) { throw '临时发布目录超出指定目录。' }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
