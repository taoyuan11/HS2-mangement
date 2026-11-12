[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$BuildDirectory = '',
    [string]$PackageDirectory = '',
    [string]$PhysicsDirectory = '',
    [string]$FbxDirectory = '',
    [switch]$SkipTests,
    [switch]$SkipUi,
    [switch]$Run,
    [string]$DataDirectory = '',
    [string]$ResultFile = '',
    [ValidateRange(1, 32)][int]$Jobs = [Math]::Min(4, [Environment]::ProcessorCount)
)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if ($SkipUi -and ($Run -or $ResultFile)) {
    throw '-Run 和 -ResultFile 需要构建 WinUI 程序，不能与 -SkipUi 同时使用。'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) {
    throw '需要 Visual Studio 2022 桌面 C++ 工作负载和 Windows SDK。'
}
$installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or !$installation) {
    throw '未找到 Visual Studio 2022 C++ 工具链。'
}
$msbuild = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
$cmake = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
if (!(Test-Path -LiteralPath $cmake)) {
    throw '请安装 Visual Studio 的 C++ CMake 工具。'
}
foreach ($tool in @($msbuild, $ctest)) {
    if (!(Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "构建工具缺失：$tool"
    }
}
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Where-Object { $_.Name -match '^10\.0\.' -and (Test-Path -LiteralPath (Join-Path $sdkRoot "bin/$($_.Name)/x64/makepri.exe")) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) {
    throw '未找到包含 MakePRI 的 Windows 10/11 SDK。'
}
if (!$BuildDirectory) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $key = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($source.ToUpperInvariant())))).Replace('-', '').Substring(0, 16)
    }
    finally {
        $hash.Dispose()
    }
    $BuildDirectory = Join-Path $env:LOCALAPPDATA "HS2Manager/CppBuild/$key"
}
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if ($BuildDirectory.StartsWith('\\') -or ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($BuildDirectory))).DriveType -eq 'Network') {
    throw 'C++ 编译产物必须放在本地磁盘。'
}
New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null
$env:MSBUILDDISABLENODEREUSE = '1'

function Invoke-BuildTool([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "构建命令失败（$LASTEXITCODE）：$Program"
    }
}

if (!$SkipUi) {
    if (!$PhysicsDirectory) {
        $PhysicsDirectory = Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/PhysX-3.4.2/bridge/Release'
        $missingPhysics = @('HS2Manager.Physics.dll', 'PhysX3_x64.dll', 'PhysX3Common_x64.dll', 'PxFoundation_x64.dll', 'PxPvdSDK_x64.dll') | Where-Object { !(Test-Path -LiteralPath (Join-Path $PhysicsDirectory $_) -PathType Leaf) }
        if ($missingPhysics) {
            & (Join-Path $source 'scripts/build/Build-NativePhysics.ps1') -Jobs $Jobs
        }
    }
    if (!$FbxDirectory) {
        $FbxDirectory = Join-Path $env:LOCALAPPDATA 'HS2Manager/NativeBuild/Fbx-2020.3.7/Release'
        $missingFbx = @('HS2Manager.FbxBridge.dll', 'libfbxsdk.dll') | Where-Object { !(Test-Path -LiteralPath (Join-Path $FbxDirectory $_) -PathType Leaf) }
        if ($missingFbx) {
            & (Join-Path $source 'scripts/build/Build-NativeFbx.ps1') -Jobs $Jobs
        }
    }
}
if (!$SkipUi) {
    $packageRoot = if ($PackageDirectory) {
        [IO.Path]::GetFullPath($PackageDirectory)
    }
    else {
        Join-Path $env:USERPROFILE '.nuget/packages'
    }
    $packages = Get-Content -LiteralPath (Join-Path $source 'src/HS2Manager.NativeApp/packages.lock.json') -Raw | ConvertFrom-Json
    foreach ($package in $packages) {
        $id = $package.id.ToLowerInvariant()
        $destination = Join-Path $packageRoot "$id/$($package.version)"
        $manifest = Join-Path $destination "$id.nuspec"
        $expected = [string]$package.sha512
        if (!$expected -or [Convert]::FromBase64String($expected).Length -ne 64) {
            throw "NuGet 锁定校验值缺失：$id"
        }
        if (Test-Path -LiteralPath $manifest) {
            [xml]$cached = Get-Content -LiteralPath $manifest -Raw
            if ($cached.package.metadata.id -ne $package.id -or $cached.package.metadata.version -ne $package.version) {
                throw "NuGet 缓存版本不符：$destination"
            }
            $cachedHash = Join-Path $destination "$id.$($package.version).nupkg.sha512"
            if ((Test-Path -LiteralPath $cachedHash) -and (Get-Content -LiteralPath $cachedHash -Raw).Trim() -ne $expected) {
                throw "NuGet 缓存校验值不符：$destination"
            }
            continue
        }
        $uri = "https://api.nuget.org/v3-flatcontainer/$id/$($package.version)/$id.$($package.version).nupkg"
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
        $staging = Join-Path $packageRoot (".hs2-restore-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $staging | Out-Null
        try {
            $archive = Join-Path $staging 'package.zip'
            Write-Output "Restoring $($package.id) $($package.version)"
            Invoke-WebRequest -Uri $uri -OutFile $archive -UseBasicParsing
            $sha = [Security.Cryptography.SHA512]::Create()
            $stream = [IO.File]::OpenRead($archive)
            try {
                $actual = [Convert]::ToBase64String($sha.ComputeHash($stream))
            }
            finally {
                $stream.Dispose()
                $sha.Dispose()
            }
            if ($actual -ne $expected) {
                throw "NuGet 包校验失败：$id"
            }
            $expanded = Join-Path $staging 'expanded'
            Expand-Archive -LiteralPath $archive -DestinationPath $expanded
            [IO.File]::WriteAllText((Join-Path $expanded "$id.$($package.version).nupkg.sha512"), $expected, [Text.Encoding]::ASCII)
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            if (Test-Path -LiteralPath $destination) {
                throw "发现不完整的 NuGet 缓存，请检查：$destination"
            }
            $expandedPath = [IO.Path]::GetFullPath($expanded)
            $destinationPath = [IO.Path]::GetFullPath($destination)
            $packagePrefix = [IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (!$expandedPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or !$destinationPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'NuGet 发布路径越界。'
            }
            [IO.Directory]::Move($expandedPath, $destinationPath)
        }
        finally {
            $resolved = [IO.Path]::GetFullPath($staging)
            $allowed = [IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
                throw '恢复临时目录越界。'
            }
            if (Test-Path -LiteralPath $resolved) {
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
}
$native = Join-Path $BuildDirectory 'modules'
$cmakeArguments = @('-S', $source, '-B', $native, '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DCMAKE_GENERATOR_INSTANCE=$installation", "-DCMAKE_SYSTEM_VERSION=$($sdk.Name)", '-DBUILD_TESTING=ON')
if ($FbxDirectory) {
    $cmakeArguments += ('-DHS2_FBX_BRIDGE=' + (Join-Path ([IO.Path]::GetFullPath($FbxDirectory)) 'HS2Manager.FbxBridge.dll'))
}
else {
    $cmakeArguments += '-DHS2_FBX_BRIDGE='
}
Invoke-BuildTool $cmake $cmakeArguments
Invoke-BuildTool $cmake @('--build', $native, '--config', $Configuration, '--parallel', "$Jobs", '--', '/nr:false', '/v:minimal')
if (!$SkipTests) {
    Invoke-BuildTool $ctest @('--test-dir', $native, '-C', $Configuration, '--parallel', "$Jobs", '--output-on-failure')
}
if (!$SkipUi) {
    $uiBuildArguments = @((Join-Path $source 'HS2Manager.Native.sln'), "/p:Configuration=$Configuration", '/p:Platform=x64', "/p:NativeBuildRoot=$BuildDirectory", '/p:NativeModulesBuilt=true', ("/p:NativePackageRoot=" + $packageRoot.TrimEnd('\', '/') + '\'), "/p:WindowsTargetPlatformVersion=$($sdk.Name)", "/p:NativeBuildParallelism=$Jobs", "/m:$Jobs", '/nr:false', '/nologo', '/v:minimal')
    $uiErrorLog = Join-Path $BuildDirectory ("msbuild-errors-" + [Guid]::NewGuid().ToString('N') + '.log')
    try {
        try {
            Invoke-BuildTool $msbuild ($uiBuildArguments + "/flp:ErrorsOnly;Encoding=UTF-8;LogFile=$uiErrorLog")
        }
        catch {
            if (!(Test-Path -LiteralPath $uiErrorLog -PathType Leaf) -or
                !(Select-String -LiteralPath $uiErrorLog -Pattern '\berror\s+(C2859|LNK1209)\s*:' -Quiet)) {
                throw
            }
            # PCH/编译 PDB 或增量链接 PDB 不一致时，成套重建当前配置的 WinUI 产物。
            # NativeModulesBuilt 保留已完成的 CMake 构建；只重试一次，其他失败照常抛出。
            Write-Warning '检测到 WinUI 调试数据库缓存不一致（C2859/LNK1209），正在重建当前配置。'
            Invoke-BuildTool $msbuild ($uiBuildArguments + '/t:Rebuild')
        }
    }
    finally {
        if (Test-Path -LiteralPath $uiErrorLog -PathType Leaf) {
            Remove-Item -LiteralPath $uiErrorLog -Force
        }
    }
    $executable = Join-Path $BuildDirectory "bin/$Configuration/HS2Manager.exe"
    $runtime = @{Package = (Split-Path -Parent $executable)
        VisualStudioDirectory = $installation
    }
    if ($PhysicsDirectory) {
        $runtime.PhysicsDirectory = $PhysicsDirectory
    }
    if ($FbxDirectory) {
        $runtime.FbxDirectory = $FbxDirectory
    }
    & (Join-Path $source 'scripts/package/Copy-CppRuntime.ps1') @runtime
    Copy-Item -LiteralPath (Join-Path $native "src/HS2Manager.NativeAgent/$Configuration/HS2Agent.exe") -Destination (Join-Path (Split-Path -Parent $executable) 'HS2Agent.exe')
    & (Join-Path $source 'scripts/package/Test-CppPackage.ps1') -Package (Split-Path -Parent $executable) -DevelopmentOutput
    # GUI 冒烟测试仅由 tests/ui/Invoke-CppWinUiSmoke.ps1 显式运行，构建不打开应用窗口。
    if ($ResultFile) {
        $resultPath = [IO.Path]::GetFullPath($ResultFile)
        New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null
        @{Executable = $executable
            Package = (Split-Path -Parent $executable)
            BuildDirectory = $BuildDirectory
            Configuration = $Configuration
        } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    }
    Write-Output "C++ WinUI 3: $executable"
    if ($Run) {
        $arguments = @()
        if ($DataDirectory) {
            $arguments = @('--data', [IO.Path]::GetFullPath($DataDirectory))
        }
        # 从源码启动时在前台同步等待，不创建脱离当前任务的构建或监视服务。
        & $executable @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "HS2 Manager 退出码：$LASTEXITCODE"
        }
    }
}
