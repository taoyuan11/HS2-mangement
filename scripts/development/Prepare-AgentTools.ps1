[CmdletBinding()]
param(
    [string]$BlenderDirectory = '',
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$toolRoot = Join-Path $repository '.tools/agent-tools'
$editor = Join-Path $toolRoot 'Unity-2018.4.11f1/Editor/Unity.exe'
$project = Join-Path $toolRoot 'ModdingTool'
$revision = '67b460fc880e8c46164781967d2bb06a5d10896d'
$installer = Join-Path $toolRoot 'UnitySetup64-2018.4.11f1.exe'
$installerHash = '96437901C9C434CE00F6E9BEFBC83EB38913D908869527C85F01AE5ED9D34237'
$installerUrl = 'https://download.unity3d.com/download_unity/7098af2f11ea/Windows64EditorInstaller/UnitySetup64-2018.4.11f1.exe'
if (!$Check) {
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    if (!(Test-Path -LiteralPath $editor -PathType Leaf)) {
        if (!(Test-Path -LiteralPath $installer -PathType Leaf)) {
            Invoke-WebRequest -Uri $installerUrl -OutFile $installer -UseBasicParsing
        }
        if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $installerHash) {
            throw 'Unity 官方安装包校验失败。'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $installer
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Unity Technologies') {
            throw 'Unity 官方签名无效。'
        }
        $sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe'
        if (!(Test-Path -LiteralPath $sevenZip -PathType Leaf)) {
            throw '需要已安装的 7-Zip 来展开官方 Editor 安装包。'
        }
        & $sevenZip x $installer ("-o" + (Join-Path $toolRoot 'Unity-2018.4.11f1')) -y -bsp0 -bso0
        if ($LASTEXITCODE -ne 0) {
            throw 'Unity Editor 展开失败。'
        }
    }
    if (!(Test-Path -LiteralPath $project -PathType Container)) {
        & git clone --depth 1 --branch release 'https://github.com/hooh-hooah/ModdingTool.git' $project
        if ($LASTEXITCODE -ne 0) {
            throw 'ModdingTool 下载失败。'
        }
    }
}
if (!(Test-Path -LiteralPath $editor -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -PathType Leaf)) {
    throw 'Unity Editor 或 ModdingTool 尚未准备完成。'
}
$safeProject = $project.Replace('\', '/')
$actualRevision = & git -c "safe.directory=$safeProject" -C $project rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $revision) {
    throw 'ModdingTool 版本不符合固定基线，请检查项目目录。'
}
$result = [ordered]@{
    unity_path = $editor
    modding_project = $project
    modding_revision = $actualRevision
    installer_url = $installerUrl
    installer_sha256 = $installerHash
    unity_activation = 'not_checked'
}
$packageArguments = @((Join-Path $PSScriptRoot 'Prepare-AgentPackages.py'), '--project', $project)
if ($Check) {
    $packageArguments += '--check'
}
& python @packageArguments
if ($LASTEXITCODE -ne 0) {
    throw 'ModdingTool 本地依赖准备或检查失败。'
}
if ($BlenderDirectory) {
    $blender = Join-Path ([IO.Path]::GetFullPath($BlenderDirectory)) 'blender.exe'
    if (!(Test-Path -LiteralPath $blender -PathType Leaf)) {
        throw 'Blender 目录中没有 blender.exe。'
    }
    $result.blender_path = $blender
}
$json = $result | ConvertTo-Json
if (!$Check) {
    [IO.File]::WriteAllText((Join-Path $toolRoot 'toolchain.json'), $json, [Text.UTF8Encoding]::new($false))
}
Write-Output $json
