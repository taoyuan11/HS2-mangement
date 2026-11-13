[CmdletBinding()]
param([string]$Directory = (Join-Path $PSScriptRoot '../../.tools/ci'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$installer = Join-Path $root 'fbx202037_fbxsdk_vs2022_win.exe'
$fbxHash = '6911DF36F6430DA6472DC48859A419F11AD6A69C376416EC6A7E369DD5A712A6'
if (!(Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest -Uri 'https://damassets.autodesk.net/content/dam/autodesk/www/files/fbx202037_fbxsdk_vs2022_win.exe' -OutFile $installer -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $fbxHash) {
    throw 'FBX SDK 下载校验失败。'
}
$fbx = Join-Path $root 'fbx-2020.3.7'
# 官方 NSIS 包只解压所需头文件、x64 库和许可，不运行安装程序。
$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
$sevenZipPath = if ($sevenZip) {
    $sevenZip.Source
}
else {
    Join-Path $env:ProgramFiles '7-Zip/7z.exe'
}
& $sevenZipPath x $installer "-o$fbx" '-y' 'include/*' 'lib/x64/*' 'License.rtf' | Out-Null
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $fbx 'include/fbxsdk.h'))) {
    throw 'FBX SDK 解压失败。'
}
$physx = Join-Path $root 'physx-3.4'
$commit = '5e42a5f112351a223c19c17bb331e6c55037b8eb'
$gitOptions = @('-c', ('safe.directory=' + $physx.Replace('\', '/')), '-C', $physx)
if (!(Test-Path -LiteralPath (Join-Path $physx '.git'))) {
    & git init $physx
    if ($LASTEXITCODE -ne 0) {
        throw 'PhysX 源码目录初始化失败。'
    }
}
$remote = & git @gitOptions config --get remote.origin.url
if (!$remote) {
    & git @gitOptions remote add origin https://github.com/NVIDIAGameWorks/PhysX-3.4.git
    if ($LASTEXITCODE -ne 0) {
        throw 'PhysX 源码地址配置失败。'
    }
}
elseif ($remote -ne 'https://github.com/NVIDIAGameWorks/PhysX-3.4.git') {
    throw 'PhysX 缓存的来源地址不符。'
}
& git @gitOptions fetch --depth=1 origin $commit
if ($LASTEXITCODE -ne 0) {
    throw 'PhysX 固定版本下载失败。'
}
& git @gitOptions checkout --detach --force $commit
if ($LASTEXITCODE -ne 0 -or (& git @gitOptions rev-parse HEAD) -ne $commit) {
    throw 'PhysX 提交校验失败。'
}
& git @gitOptions fsck --no-reflogs
if ($LASTEXITCODE -ne 0) {
    throw 'PhysX 对象完整性检查失败。'
}
Write-Output "FBX SDK: $fbx"
Write-Output "PhysX: $physx"
