[CmdletBinding()]
param(
    [string]$GamePath='Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard='',
    [string]$FbxSdkRoot='',
    [string]$PhysXSource='',
    [string]$OutputDirectory='',
    [switch]$SkipArchive,
    [string]$BuildDirectory=''
)
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:MSBUILDDISABLENODEREUSE='1'
$packageName='HS2Manager-1.4.0-cpp-win-x64'
$destination=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $source ('artifacts/release/'+$packageName)}
$destination=$destination.TrimEnd('\','/')
$parent=Split-Path -Parent $destination
if(!$parent -or $destination -eq [IO.Path]::GetPathRoot($destination).TrimEnd('\','/')){throw '输出目录必须是一个子文件夹。'}
if(Test-Path -LiteralPath $destination){throw "输出目录已存在，请指定新的输出位置：$destination"}
$archive=$destination+'.zip'
if(!$SkipArchive -and (Test-Path -LiteralPath $archive)){throw "压缩包已存在，请指定新的输出位置：$archive"}
$id=[Guid]::NewGuid().ToString('N')
$result=Join-Path $env:LOCALAPPDATA ('HS2Manager/CppBuild/publish-'+$id+'.json')
$staging=Join-Path $parent ('.hs2-cpp-package-'+$id)
$archiveStaging=Join-Path $parent ('.hs2-cpp-archive-'+$id+'.zip')
$smokeRoot=Join-Path $env:LOCALAPPDATA ('HS2Manager/CppBuild/package-smoke-'+$id)
function Get-SafeFiles([string]$Directory) {
    if((Get-Item -LiteralPath $Directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "发布输入不能是链接：$Directory"}
    foreach($item in Get-ChildItem -LiteralPath $Directory -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "发布输入不能包含链接：$($item.FullName)"}
        if($item.PSIsContainer){Get-SafeFiles $item.FullName}else{$item}
    }
}
function Remove-OwnedStaging {
    $resolved=[IO.Path]::GetFullPath($staging)
    if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($parent) -or [IO.Path]::GetFileName($resolved) -ne ('.hs2-cpp-package-'+$id)){throw '发布清理目录越界。'}
    if(Test-Path -LiteralPath $resolved){
        if((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '发布临时目录不能是链接。'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
function Remove-OwnedSmoke {
    $resolved=[IO.Path]::GetFullPath($smokeRoot)
    $allowed=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'HS2Manager/CppBuild'))
    if([IO.Path]::GetDirectoryName($resolved) -ne $allowed -or [IO.Path]::GetFileName($resolved) -ne ('package-smoke-'+$id)){throw '发布启动检查目录越界。'}
    if(Test-Path -LiteralPath $resolved){
        if((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '发布启动检查目录不能是链接。'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
try {
    $fbx=@{};if($FbxSdkRoot){$fbx.SdkRoot=$FbxSdkRoot}
    $physics=@{};if($PhysXSource){$physics.Source=$PhysXSource}
    & (Join-Path $PSScriptRoot 'Build-NativeFbx.ps1') @fbx
    & (Join-Path $PSScriptRoot 'Build-NativePhysics.ps1') @physics
    $arguments=@{Configuration='Release';ResultFile=$result;SkipTests=$SkipTests}
    if($BuildDirectory){$arguments.BuildDirectory=$BuildDirectory}
    & (Join-Path $source 'build-cpp.ps1') @arguments
    $build=Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if(!(Test-Path -LiteralPath $build.Executable -PathType Leaf)){throw '原生构建没有生成程序。'}
    if($PreviewTest){
        if(!(Test-Path -LiteralPath (Join-Path $GamePath 'abdata/chara/oo_base.unity3d'))){throw '预览检查需要有效的本机游戏资源。'}
        if(!$PreviewCard){$PreviewCard=Get-ChildItem -LiteralPath (Join-Path $GamePath 'UserData/chara/female') -Filter '*.png' -File | Select-Object -First 1 -ExpandProperty FullName}
        if(!$PreviewCard -or !(Test-Path -LiteralPath $PreviewCard -PathType Leaf)){throw '请通过 -PreviewCard 指定用于预览检查的人物卡。'}
        $check=Join-Path $build.BuildDirectory 'modules/src/HS2Manager.NativeRendering/Release/hs2_render_frame_checks.exe'
        $checkData=Join-Path $env:LOCALAPPDATA 'HS2Manager/CppValidation/Data'
        $checkOutput=Join-Path $env:LOCALAPPDATA ('HS2Manager/CppValidation/Frames/'+$id)
        & $check $GamePath $checkData (Join-Path $build.Package 'classdata.tpk') $PreviewCard $checkOutput (Join-Path $build.Package 'native/win-x64')
        if($LASTEXITCODE -ne 0){throw '真实人物预览检查失败。'}
    }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    New-Item -ItemType Directory -Path $staging | Out-Null
    $packageRoot=[IO.Path]::GetFullPath($build.Package).TrimEnd('\','/')
    foreach($file in Get-SafeFiles $packageRoot){
        $relative=$file.FullName.Substring($packageRoot.Length).TrimStart('\','/')
        if($relative -match '^(Data|Logs|Covers|CardBackups|Operations|NativeCpp|NativePreview[^\\/]*|UnityPreview)[\\/]' -or
            $file.Extension -in '.pdb','.ilk','.exp','.lib','.iobj','.ipdb','.log' -or
            $file.Name -match '^(native-.*error.*|hs2_.*_(checks|fixtures)\.exe|library\.db.*|settings\.json)$'){continue}
        $target=Join-Path $staging $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    $data=Join-Path $staging 'Data'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    '本目录用于保存本机人物库、设置、封面、备份和日志。请整体备份。NAS 安装自动迁移到本机 LocalAppData。' | Set-Content -LiteralPath (Join-Path $data 'README.txt') -Encoding UTF8
    $docs=Join-Path $source 'docs'
    foreach($file in Get-SafeFiles $docs){
        $relative=$file.FullName.Substring($docs.Length).TrimStart('\','/')
        $target=Join-Path $staging ('docs/'+$relative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    & (Join-Path $PSScriptRoot 'Test-CppPackage.ps1') -Package $staging
    if(!$SkipTests){
        # Validate the staged payload from a local copy even when publishing to NAS.
        # The test always uses its own --data directory and waits for its process.
        Copy-Item -LiteralPath $staging -Destination $smokeRoot -Recurse
        & (Join-Path $source 'tests/Invoke-CppWinUiSmoke.ps1') -Executable (Join-Path $smokeRoot 'HS2Manager.exe')
        Remove-OwnedSmoke
    }
    $manifest=foreach($file in Get-ChildItem -LiteralPath $staging -File -Recurse){[ordered]@{Path=$file.FullName.Substring($staging.Length).TrimStart('\','/');Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}}
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'package-files.json') -Encoding UTF8
    [IO.Directory]::Move($staging,$destination)
    if(!$SkipArchive){
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($destination,$archiveStaging,[IO.Compression.CompressionLevel]::Optimal,$true)
        [IO.File]::Move($archiveStaging,$archive)
        Write-Output "便携压缩包：$archive"
    }
    Write-Output "纯 C++ 便携目录：$destination"
} finally {
    Remove-OwnedStaging
    Remove-OwnedSmoke
    if(Test-Path -LiteralPath $archiveStaging){Remove-Item -LiteralPath $archiveStaging -Force}
    if(Test-Path -LiteralPath $result){Remove-Item -LiteralPath $result -Force}
}
