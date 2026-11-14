param([string]$SdkRoot = (Join-Path $PSScriptRoot '../../.tools/fbx-native-2020.3.7'), [string]$BuildDirectory,
    [ValidateRange(1, 32)][int]$Jobs = [Math]::Min(4, [Environment]::ProcessorCount))
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# 保留手动配置的 SDK；首次构建时与 CI 共用固定版本恢复逻辑。
if (!$PSBoundParameters.ContainsKey('SdkRoot') -and !(Test-Path -LiteralPath $SdkRoot)) {
    $SdkRoot = (& (Join-Path $PSScriptRoot 'Restore-NativeDependencies.ps1') -Dependency Fbx).FbxSdkRoot
}
foreach ($relative in @('include/fbxsdk.h', 'lib/x64/release/libfbxsdk.lib', 'lib/x64/release/libfbxsdk.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $SdkRoot $relative) -PathType Leaf)) {
        throw "FBX SDK 2020.3.7 is missing or incomplete: $SdkRoot. Use -SdkRoot to specify the SDK directory."
    }
}
if (!$BuildDirectory) {
    $BuildDirectory = Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/Fbx-2020.3.7'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = $null
if (Test-Path -LiteralPath $vswhere) {
    $vs = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Visual Studio C++ installation discovery failed.'
    }
}
if (!$vs) {
    $candidate = Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio/2022/Community'
    if (Test-Path -LiteralPath (Join-Path $candidate 'VC/Tools/MSVC')) {
        $vs = $candidate
    }
}
if (!$vs) {
    throw 'Visual Studio 2022 with the desktop C++ workload is required.'
}
$cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) {
    $cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if (!$cmakeCommand) {
        throw 'CMake is required. Install the Visual Studio C++ CMake tools or add CMake to PATH.'
    }
    $cmake = $cmakeCommand.Source
}
& $cmake -S (Join-Path $repo 'native/HS2Manager.FbxBridge') -B $BuildDirectory -G 'Visual Studio 17 2022' -A x64 ('-DCMAKE_GENERATOR_INSTANCE=' + $vs) ('-DFBX_SDK_ROOT=' + [IO.Path]::GetFullPath($SdkRoot))
if ($LASTEXITCODE -ne 0) {
    throw 'Native FBX configuration failed.'
}
& $cmake --build $BuildDirectory --config Release --parallel $Jobs -- /nr:false
if ($LASTEXITCODE -ne 0) {
    throw 'Native FBX compilation failed.'
}
Write-Output (Join-Path $BuildDirectory 'Release/HS2Manager.FbxBridge.dll')
