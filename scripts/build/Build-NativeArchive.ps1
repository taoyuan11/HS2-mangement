[CmdletBinding()]
param(
    [string]$BuildDirectory = (Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/7zip-26.06')
)
$ErrorActionPreference = 'Stop'
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$sourceRoot = Join-Path $buildRoot 'source'
$library = Join-Path $sourceRoot 'CPP/7zip/Bundles/Format7zF/x64/7z.dll'
$stamp = Join-Path $buildRoot 'complete-26.06.txt'
if ((Test-Path -LiteralPath $library -PathType Leaf) -and (Test-Path -LiteralPath $stamp -PathType Leaf)) {
    Write-Output $library
    return
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or !$installation) {
    throw '7-Zip 构建需要 Visual Studio 2022 C++ 工具链。'
}
$cmake = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$archive = Join-Path $buildRoot 'source.tar.xz'
$expected = 'A4E374C8F864F51DF48C2CFD0E1413EC3B2EA030DEAE852D6A655402EBB61AC4'
if (!(Test-Path -LiteralPath $archive -PathType Leaf)) {
    Invoke-WebRequest -Uri 'https://github.com/ip7z/7zip/releases/download/26.06/7z2606-src.tar.xz' -OutFile $archive -UseBasicParsing
}
# CMake 启动的 Windows PowerShell 可能继承 PowerShell 7 模块路径，直接用 .NET 校验。
$sha = [Security.Cryptography.SHA256]::Create()
$stream = $null
try {
    $stream = [IO.File]::OpenRead($archive)
    $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
}
finally {
    if ($null -ne $stream) {
        $stream.Dispose()
    }
    $sha.Dispose()
}
if ($actual -ne $expected) {
    throw "7-Zip 26.06 源码校验失败：$archive"
}
if (!(Test-Path -LiteralPath (Join-Path $sourceRoot 'CPP/7zip/Archive/IArchive.h'))) {
    New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
    Push-Location $sourceRoot
    try {
        & $cmake -E tar xf $archive
        if ($LASTEXITCODE -ne 0) {
            throw '7-Zip 源码解压失败。'
        }
    }
    finally {
        Pop-Location
    }
}
$project = Join-Path $sourceRoot 'CPP/7zip/Bundles/Format7zF'
$environment = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$log = Join-Path $buildRoot 'build.log'
$batch = Join-Path $buildRoot 'build-native.cmd'
[IO.File]::WriteAllLines($batch, @('@echo off', ('call "' + $environment + '" >nul 2>&1'), 'if errorlevel 1 exit /b %errorlevel%', ('nmake /nologo PLATFORM=x64 MY_DYNAMIC_LINK=1 > "' + $log + '" 2>&1')), [Text.Encoding]::Default)
# 使用完整 Format7zF，包含 ZIP、7Z 与 RAR 解码；构建工具同步退出，不留下后台任务。
Push-Location $project
try {
    & $env:ComSpec /d /c $batch
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $library -PathType Leaf)) {
        Get-Content -LiteralPath $log -Tail 35
        throw "7-Zip 构建失败，日志：$log"
    }
}
finally {
    Pop-Location
}
[IO.File]::WriteAllText($stamp, $expected, [Text.Encoding]::ASCII)
Write-Output $library
