[CmdletBinding()]
param(
    [string]$Directory = (Join-Path $PSScriptRoot '../../.tools/ci'),
    [ValidateSet('All', 'PhysX', 'Fbx')][string]$Dependency = 'All'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$physx = Join-Path $root 'physx-3.4'
$fbx = Join-Path $root 'fbx-2020.3.7'
$commit = '5e42a5f112351a223c19c17bb331e6c55037b8eb'
$physxUrl = 'https://github.com/NVIDIAGameWorks/PhysX-3.4.git'
$fbxHash = '6911DF36F6430DA6472DC48859A419F11AD6A69C376416EC6A7E369DD5A712A6'
$staging = Join-Path $root ('.restore-' + [Guid]::NewGuid().ToString('N'))

function Test-DependencyFiles([string]$Path, [string[]]$Files) {
    foreach ($file in $Files) {
        if (!(Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)) {
            return $false
        }
    }
    return $true
}

function Move-DependencyDirectory([string]$Source, [string]$Destination) {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $stagingPrefix = [IO.Path]::GetFullPath($staging).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$sourcePath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($destinationPath) -ne $root.TrimEnd('\', '/')) {
        throw '依赖恢复目标目录越界。'
    }
    [IO.Directory]::Move($sourcePath, $destinationPath)
}

try {
    if ($Dependency -in 'All', 'PhysX') {
        if (!(Get-Command git -ErrorAction SilentlyContinue)) {
            throw 'PhysX 自动恢复需要 Git for Windows；也可通过 -Source 指定已有的 PhysX 3.4.2 源码。'
        }
        $physxFiles = @('PhysX_3.4/Include/PxPhysicsVersion.h', 'PhysX_3.4/Source/compiler/vc15win64/PhysX.sln', 'PxShared/src/foundation/include/PsAllocator.h')
        if (!(Test-Path -LiteralPath $physx)) {
            Write-Host 'Restoring NVIDIA PhysX 3.4.2 (pinned source commit)...'
            $checkout = Join-Path $staging 'physx'
            & git init --quiet $checkout
            if ($LASTEXITCODE -ne 0) {
                throw 'PhysX 源码目录初始化失败。'
            }
            $gitOptions = @('-c', ('safe.directory=' + $checkout.Replace('\', '/')), '-C', $checkout)
            & git @gitOptions remote add origin $physxUrl
            if ($LASTEXITCODE -ne 0) {
                throw 'PhysX 源码地址配置失败。'
            }
            & git @gitOptions fetch --quiet --depth=1 origin $commit
            if ($LASTEXITCODE -ne 0) {
                throw 'PhysX 固定版本下载失败，请检查 GitHub 网络连接后重试。'
            }
            & git @gitOptions checkout --quiet --detach $commit
            if ($LASTEXITCODE -ne 0) {
                throw 'PhysX 源码检出失败。'
            }
        }
        else {
            $checkout = $physx
        }
        # 缓存可离线复用；不强制重置用户修改，也不以不完整目录冒充已恢复的 SDK。
        if (!(Test-Path -LiteralPath (Join-Path $checkout '.git')) -or !(Test-DependencyFiles $checkout $physxFiles)) {
            throw "PhysX 缓存不完整，请检查或移走此目录后重试：$checkout"
        }
        $gitOptions = @('-c', ('safe.directory=' + $checkout.Replace('\', '/')), '-C', $checkout)
        $remote = & git @gitOptions config --get remote.origin.url
        if ($LASTEXITCODE -ne 0 -or $remote -ne $physxUrl) {
            throw "PhysX 缓存的来源地址不符：$checkout"
        }
        $head = & git @gitOptions rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $head -ne $commit) {
            throw "PhysX 缓存的提交不符：$checkout"
        }
        $changes = & git @gitOptions status --porcelain
        if ($LASTEXITCODE -ne 0 -or $changes) {
            throw "PhysX 缓存包含本地修改，请使用 -Source 显式指定，或移走此目录后重试：$checkout"
        }
        & git @gitOptions fsck --no-reflogs | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "PhysX 对象完整性检查失败：$checkout"
        }
        if ($checkout -ne $physx) {
            Move-DependencyDirectory $checkout $physx
        }
    }
    if ($Dependency -in 'All', 'Fbx') {
        $fbxFiles = @('include/fbxsdk.h', 'lib/x64/release/libfbxsdk.lib', 'lib/x64/release/libfbxsdk.dll', 'License.rtf')
        $installer = Join-Path $root 'fbx202037_fbxsdk_vs2022_win.exe'
        if (!(Test-Path -LiteralPath $installer -PathType Leaf) -or !(Test-DependencyFiles $fbx $fbxFiles)) {
            $sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
            $sevenZipPath = if ($sevenZip) {
                $sevenZip.Source
            }
            else {
                Join-Path $env:ProgramFiles '7-Zip/7z.exe'
            }
            if (!(Test-Path -LiteralPath $sevenZipPath -PathType Leaf)) {
                throw 'FBX SDK 自动恢复需要 7-Zip。请安装 7-Zip，或通过 -SdkRoot 指定已解压的 FBX SDK 2020.3.7。'
            }
            New-Item -ItemType Directory -Path $staging -Force | Out-Null
            if (!(Test-Path -LiteralPath $installer -PathType Leaf)) {
                Write-Host 'Restoring Autodesk FBX SDK 2020.3.7...'
                $download = Join-Path $staging 'fbxsdk.exe'
                Invoke-WebRequest -Uri 'https://damassets.autodesk.net/content/dam/autodesk/www/files/fbx202037_fbxsdk_vs2022_win.exe' -OutFile $download -UseBasicParsing
                if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $fbxHash) {
                    throw 'FBX SDK 下载校验失败，请重试。'
                }
                [IO.File]::Move($download, $installer)
            }
        }
        if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $fbxHash) {
            throw "FBX SDK 下载校验失败，请移走此文件后重试：$installer"
        }
        if (!(Test-DependencyFiles $fbx $fbxFiles)) {
            if (Test-Path -LiteralPath $fbx) {
                throw "FBX SDK 缓存不完整，请检查或移走此目录后重试：$fbx"
            }
            $expanded = Join-Path $staging 'fbx'
            # 官方 NSIS 包只解压所需头文件、x64 库和许可，不运行安装程序。
            & $sevenZipPath x $installer "-o$expanded" '-y' 'include/*' 'lib/x64/*' 'License.rtf' | Out-Host
            if ($LASTEXITCODE -ne 0 -or !(Test-DependencyFiles $expanded $fbxFiles)) {
                throw 'FBX SDK 解压失败。'
            }
            Move-DependencyDirectory $expanded $fbx
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $root.TrimEnd('\', '/') -or
        ![IO.Path]::GetFileName($resolved).StartsWith('.restore-')) {
        throw '依赖恢复临时目录越界。'
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
[pscustomobject]@{
    PhysXSource = $physx
    FbxSdkRoot = $fbx
}
