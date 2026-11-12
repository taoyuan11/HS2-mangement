[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$PhysicsDirectory = (Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/PhysX-3.4.2/bridge/Release'),
    [string]$FbxDirectory = (Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/Fbx-2020.3.7/Release'),
    [string]$VisualStudioDirectory = ''
)
$ErrorActionPreference = 'Stop'
$packagePath = [IO.Path]::GetFullPath($Package)
if (!(Test-Path -LiteralPath $packagePath -PathType Container)) {
    throw 'C++ package directory is missing.'
}
$native = Join-Path $packagePath 'native/win-x64'
New-Item -ItemType Directory -Path $native -Force | Out-Null
$archiveLibrary = & (Join-Path $PSScriptRoot '../build/Build-NativeArchive.ps1')
Copy-Item -LiteralPath $archiveLibrary -Destination (Join-Path $native '7z.dll')
foreach ($name in @('HS2Manager.Physics.dll', 'PhysX3_x64.dll', 'PhysX3Common_x64.dll', 'PxFoundation_x64.dll', 'PxPvdSDK_x64.dll')) {
    $source = Join-Path $PhysicsDirectory $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Native cloth runtime is missing: $source. Run scripts/build/Build-NativePhysics.ps1."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $native $name)
}
foreach ($name in @('HS2Manager.FbxBridge.dll', 'libfbxsdk.dll')) {
    $source = Join-Path $FbxDirectory $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Native FBX runtime is missing: $source. Run scripts/build/Build-NativeFbx.ps1."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $native $name)
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = $VisualStudioDirectory
if (!$installation) {
    if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual Studio C++ runtime discovery tool is missing.'
    }
    $installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$installation) {
        throw 'Visual Studio C++ runtime discovery failed.'
    }
}
$crt = Get-ChildItem -LiteralPath (Join-Path $installation 'VC/Redist/MSVC') -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/Microsoft.VC143.CRT' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
if (!$crt) {
    throw 'Visual Studio VC143 redistributable files are missing.'
}
Get-ChildItem -LiteralPath $crt -Filter '*.dll' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $packagePath }
foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $source = Join-Path $crt $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Redistributable runtime file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $native $name)
}
Get-ChildItem -LiteralPath $native -File -Filter '*.dll' | Sort-Object Name | ForEach-Object {
    [ordered]@{File = $_.Name
        SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        Bytes = $_.Length
    }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packagePath 'native-runtime.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../src/HS2Manager.NativeScene/Resources/classdata.tpk') -Destination (Join-Path $packagePath 'classdata.tpk')
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('../../' + $name)) -Destination $packagePath
}
$converter = Join-Path $packagePath 'Resources/ModelExport'
New-Item -ItemType Directory -Path $converter -Force | Out-Null
foreach ($name in @('BlenderImport.py', 'BlenderNative.py', 'UnrealImport.py', 'UnrealNative.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('../../src/HS2Manager.NativeExport/Resources/ModelExport/' + $name)) -Destination (Join-Path $converter $name)
}
$licenses = Join-Path $packagePath 'licenses'
$agentResources = Join-Path $packagePath 'Resources/Agent'
New-Item -ItemType Directory -Path $agentResources -Force | Out-Null
foreach ($name in @('BlenderAsset.py', 'HS2AgentBuilder.cs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('../../src/HS2Manager.NativeAgent/Resources/' + $name)) -Destination (Join-Path $agentResources $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../skills') -Destination $packagePath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../agent.example.json') -Destination $packagePath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../agent.mcp.example.toml') -Destination $packagePath
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($name in @('7zip-LICENSE.txt', '7zip-LGPL.txt', '7zip-unRAR.txt', 'AssetRipper-Tpk.txt', 'Autodesk-FBX-SDK-2020.3.7.rtf', 'CppWinRT-LICENSE.txt', 'LZMA-PUBLIC-DOMAIN.txt', 'miniz-LICENSE.txt', 'nlohmann-json-LICENSE.txt', 'NVIDIA-PhysX-3.4.txt', 'sqlite-LICENSE.txt', 'Unity-PostProcessing.md', 'WindowsAppSDK-LICENSE.txt', 'WindowsAppSDK-NOTICE.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('../../licenses/' + $name)) -Destination (Join-Path $licenses $name)
}
