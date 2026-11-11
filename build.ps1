[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard = '',
    [string]$FbxSdkRoot = '',
    [string]$PhysXSource = '',
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
$fastDotnet = Join-Path $env:LOCALAPPDATA 'HS2Manager/BuildTools/dotnet-10.0.403/dotnet.exe'
if(Test-Path -LiteralPath $fastDotnet){$localDotnet=$fastDotnet}
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { throw '需要 .NET 10 SDK。请运行 bootstrap-dotnet.ps1，或安装官方 .NET 10 SDK。' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
function Invoke-Dotnet([string[]]$Arguments) {
    # Prevent services from starting; do not shut down build servers owned by other sessions.
    $Arguments = @($Arguments[0], '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:BuildInParallel=false', '-m:1') + $Arguments[1..($Arguments.Length - 1)]
    $configurationIndex = [Array]::IndexOf($Arguments, '-c')
    $configuration = if ($configurationIndex -ge 0) { $Arguments[$configurationIndex + 1] } else { 'Release' }
    $buildArguments = @(Get-Hs2BuildArguments $projectRoot $configuration)
    $Arguments = @($Arguments[0]) + $buildArguments + $Arguments[1..($Arguments.Length - 1)]
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码 $LASTEXITCODE" }
}
if ($PreviewTest -and !(Test-Path -LiteralPath (Join-Path $GamePath 'abdata\chara\oo_base.unity3d'))) { throw "游戏资源目录无效：$GamePath" }
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$packageName = 'HS2Manager-1.3.0-native-win-x64'
if ($OutputDirectory) {
    $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    $OutputDirectory = $OutputDirectory.TrimEnd('\', '/')
    $releaseRoot = Split-Path -Parent $OutputDirectory
    $packageName = Split-Path -Leaf $OutputDirectory
    if (!$releaseRoot -or !$packageName) { throw '输出目录必须是一个子文件夹，不能是磁盘根目录。' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw "输出目录已存在，请先移走或重命名后重试：$OutputDirectory" }
}
# Build the independent native adapters directly from their SDK sources.
$fbxArguments=@{};if($FbxSdkRoot){$fbxArguments.SdkRoot=$FbxSdkRoot}
$physicsArguments=@{};if($PhysXSource){$physicsArguments.Source=$PhysXSource}
& (Join-Path $projectRoot 'scripts/Build-NativeFbx.ps1') @fbxArguments
& (Join-Path $projectRoot 'scripts/Build-NativePhysics.ps1') @physicsArguments
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $projectRoot 'scripts/OwnedToolProcess.cs')}
function Invoke-CheckProject([string]$ProjectName,[string[]]$CheckArguments,[int]$TimeoutSeconds=2400) {
    $project=Join-Path $projectRoot ('tests/'+$ProjectName+'/'+$ProjectName+'.csproj')
    Invoke-Dotnet @('build',$project,'-c','Release','--nologo')
    $query=@('msbuild',$project,'-nologo','-p:Configuration=Release','-getProperty:TargetPath')
    $buildArguments=@(Get-Hs2BuildArguments $projectRoot 'Release')
    if($buildArguments.Count){$query+=('-p:ArtifactsPath='+$buildArguments[1])}
    $target=& $dotnet @query
    if($LASTEXITCODE -ne 0 -or !($target -is [string]) -or !(Test-Path -LiteralPath $target)){throw ('Cannot locate check binary: '+$ProjectName)}
    $code=[HS2Manager.BuildTools.OwnedToolProcess]::Run($dotnet,(@($target)+$CheckArguments),$projectRoot,$TimeoutSeconds,$null)
    if($code -ne 0){throw ('Validation failed: '+$ProjectName+' ('+$code+')')}
}
$package = Join-Path $releaseRoot $packageName
$stagingRoot = if ([HS2Manager.Core.StoragePaths]::IsNetwork($projectRoot)) { Join-Path (Get-Hs2LocalCache $projectRoot 'Publish') 'Staging' } else { $releaseRoot }
$staging = Join-Path $stagingRoot ($packageName + '-staging-' + [Guid]::NewGuid().ToString('N'))
$delivery = Join-Path $releaseRoot ($packageName + '-staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    if (!$SkipTests) {
        Invoke-CheckProject 'HS2Manager.Checks' @('--core','--game',$GamePath)
        Invoke-CheckProject 'HS2Manager.Checks' @('--data-location')
        Invoke-CheckProject 'HS2Manager.NativeChecks' @()
        Invoke-CheckProject 'HS2Manager.UiChecks' @()
    }
    if ($PreviewTest) {
        $sampleArguments = if ($PreviewCard) { @('--card', $PreviewCard) } else { @() }
        Invoke-CheckProject 'HS2Manager.Checks' (@('--prepare-native-character','--game',$GamePath)+$sampleArguments)
        $sessionOutput=Join-Path $testRoot ('artifacts/native-session/'+[Guid]::NewGuid().ToString('N'))
        Invoke-CheckProject 'HS2Manager.NativeChecks' @('--session-check',(Join-Path $testRoot 'artifacts/checks/native-character.json'),'--output',$sessionOutput)
        Invoke-CheckProject 'HS2Manager.UiChecks' @('--native-migration-ui','--game',$GamePath)
    }
    Invoke-Dotnet @('publish', (Join-Path $projectRoot 'src\HS2Manager.App'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UseSharedCompilation=false', '-o', $staging, '--nologo')
    if (!$SkipTests) { & (Join-Path $projectRoot 'tests\Invoke-WinUiSmoke.ps1') -Executable (Join-Path $staging 'HS2Manager.exe') }
    # Publishing the application already audits all PE identities/imports and
    # exercises the bundled FBX/cloth runtimes. Repeat after adding delivery docs.
    $forbidden=@(Get-ChildItem -LiteralPath $staging -Recurse -File|Where-Object {$_.Name -match '^(Assembly-CSharp|IL|BepInEx|HS2_|HS2Manager\.Bridge|HS2Manager\.GameReference)' -or $_.Extension -in '.zipmod','.unity3d'})
    if($forbidden.Count){throw ('发布包包含游戏、插件或测试资源：'+($forbidden.Name -join ', '))}
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $staging '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $staging -Recurse
    $docs=Join-Path $staging 'docs';New-Item -ItemType Directory -Path $docs | Out-Null
    foreach($name in @('NATIVE-GPU-PREVIEW.md','NATIVE-MIGRATION-RESULTS.md','NATIVE-MIGRATION-CARDS.csv','NATIVE-PERFORMANCE.md','NATIVE-PERFORMANCE.csv','CHARACTER-EDITOR.md','CHARACTER-EDITOR-FIELDS.csv','MODEL-EXPORT.md','NAS-COMPATIBILITY.md')){
        Copy-Item -LiteralPath (Join-Path $projectRoot ('docs/'+$name)) -Destination $docs
    }
    & (Join-Path $projectRoot 'scripts/Test-NativePackage.ps1') -Package $staging -Report (Join-Path $staging 'native-package-audit.json') -LoadNative
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
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($resolvedPackage,$zip,[IO.Compression.CompressionLevel]::Optimal,$true)
        $stream=[IO.File]::OpenRead($zip);$algorithm=[Security.Cryptography.SHA256]::Create()
        try{$digest=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}
        finally{$algorithm.Dispose();$stream.Dispose()}
        Set-Content -LiteralPath ($zip + '.sha256') -Value ($digest + '  ' + [IO.Path]::GetFileName($zip)) -Encoding ascii
        Write-Output "便携包：$zip"
        Write-Output "SHA-256：$digest"
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
