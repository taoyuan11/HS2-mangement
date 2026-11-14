param([string]$Source = (Join-Path $PSScriptRoot '../../.tools/physx-3.4'), [string]$BuildDirectory,
    [ValidateRange(1, 32)][int]$Jobs = [Math]::Min(4, [Environment]::ProcessorCount))
$ErrorActionPreference = 'Stop'
if (!$BuildDirectory) {
    $BuildDirectory = Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/PhysX-3.4.2'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$localSource = Join-Path $BuildDirectory 'source'
# 显式传入的路径仍由调用者管理；默认 SDK 未准备时自动恢复固定版本。
if (!$PSBoundParameters.ContainsKey('Source') -and !(Test-Path -LiteralPath $Source)) {
    $Source = (& (Join-Path $PSScriptRoot 'Restore-NativeDependencies.ps1') -Dependency PhysX).PhysXSource
}
$header = Join-Path $Source 'PhysX_3.4/Include/PxPhysicsVersion.h'
if (!(Test-Path -LiteralPath $header)) {
    throw "The NVIDIA PhysX 3.4.2 source checkout is missing or incomplete: $Source. Use -Source to specify the SDK directory."
}
$version = [IO.File]::ReadAllText($header)
if ($version -notmatch 'PX_PHYSICS_VERSION_MAJOR 3' -or $version -notmatch 'PX_PHYSICS_VERSION_MINOR 4' -or $version -notmatch 'PX_PHYSICS_VERSION_BUGFIX 2') {
    throw 'The cloth adapter requires PhysX 3.4.2.'
}
New-Item -ItemType Directory -Path $localSource -Force | Out-Null
& robocopy.exe $Source $localSource /E /XD .git /XF PsAllocator.h /MT:$Jobs /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw 'Could not copy the PhysX build sources.'
}
# 仅兼容新版 MSVC 对标准头文件的更名，不修改求解器公式。
$allocator = Join-Path $localSource 'PxShared/src/foundation/include/PsAllocator.h'
$allocatorContent = [IO.File]::ReadAllText((Join-Path $Source 'PxShared/src/foundation/include/PsAllocator.h')).Replace('<typeinfo.h>', '<typeinfo>')
if (!(Test-Path -LiteralPath $allocator) -or [IO.File]::ReadAllText($allocator) -cne $allocatorContent) {
    [IO.File]::WriteAllText($allocator, $allocatorContent)
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
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
$cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (!(Test-Path -LiteralPath $msbuild)) {
    throw 'MSBuild from the Visual Studio C++ workload is required.'
}
if (!(Test-Path -LiteralPath $cmake)) {
    $cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if (!$cmakeCommand) {
        throw 'CMake is required. Install the Visual Studio C++ CMake tools or add CMake to PATH.'
    }
    $cmake = $cmakeCommand.Source
}
$previousCl = $env:_CL_
try {
    $env:_CL_ = '/WX- /Wv:18'
    $log = Join-Path $BuildDirectory 'build-sdk.log'
    Write-Host "Building NVIDIA PhysX 3.4.2. Log: $log"
    # 由 MSVC 通过默认属性文件选择已安装的 Windows SDK。
    # 最后导入编译设置，覆盖 SDK 中编译器与链接器共用 PDB 的旧配置。
    $sdkArguments = @(
        (Join-Path $localSource 'PhysX_3.4/Source/compiler/vc15win64/PhysX.sln'),
        '/p:Configuration=release', '/p:Platform=x64', '/p:PlatformToolset=v143',
        ('/p:ForceImportBeforeCppTargets=' + (Join-Path $PSScriptRoot 'PhysX.Build.props')),
        '/nr:false', '/v:minimal'
    )
    & $msbuild @sdkArguments '/t:PhysX;PhysXExtensions' "/m:$Jobs" *> $log
    $sdkExitCode = $LASTEXITCODE
    if ($sdkExitCode -ne 0 -and (Select-String -LiteralPath $log -Pattern '\berror\s+(C1052|C2859|LNK1209|LNK1318)\s*:' -Quiet)) {
        # 旧 PDB 损坏时先完整清理依赖，再串行重建一次，避免多个链接器争用调试数据库。
        # 分开保存日志，保留首次失败原因；其他错误不重试，重建仍失败时照常终止。
        Write-Warning "检测到 PhysX 调试数据库异常，正在清理并重建一次。原始日志：$log"
        $cleanLog = Join-Path $BuildDirectory 'build-sdk-clean.log'
        & $msbuild @sdkArguments '/t:PhysX:Clean;PhysXExtensions:Clean' /m:1 *> $cleanLog
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $cleanLog -Tail 30
            throw "PhysX cleanup failed. Log: $cleanLog"
        }
        $log = Join-Path $BuildDirectory 'build-sdk-rebuild.log'
        & $msbuild @sdkArguments '/t:PhysX;PhysXExtensions' /m:1 *> $log
        $sdkExitCode = $LASTEXITCODE
    }
    if ($sdkExitCode -ne 0) {
        Get-Content -LiteralPath $log -Tail 30
        throw "PhysX compilation failed. Log: $log"
    }
}
finally {
    $env:_CL_ = $previousCl
}
& $cmake -S (Join-Path $repo 'native/HS2Manager.Physics') -B (Join-Path $BuildDirectory 'bridge') -G 'Visual Studio 17 2022' -A x64 ('-DCMAKE_GENERATOR_INSTANCE=' + $vs) ('-DPHYSX_ROOT=' + $localSource)
if ($LASTEXITCODE -ne 0) {
    throw 'Cloth bridge configuration failed.'
}
& $cmake --build (Join-Path $BuildDirectory 'bridge') --config Release --parallel $Jobs -- /nr:false
if ($LASTEXITCODE -ne 0) {
    throw 'Cloth bridge compilation failed.'
}
Write-Output (Join-Path $BuildDirectory 'bridge/Release/HS2Manager.Physics.dll')
