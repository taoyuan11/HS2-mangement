[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard = '',
    [string]$FbxSdkRoot = '',
    [string]$PhysXSource = ''
)
$ErrorActionPreference = 'Stop'
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
if (!$desktop -or !(Test-Path -LiteralPath $desktop -PathType Container)) {
    throw '无法找到当前用户的桌面目录。'
}

$desktop = [IO.Path]::GetFullPath($desktop).TrimEnd('\', '/')
$output = Join-Path $desktop 'HS2Manager'
$staging = Join-Path $desktop ('HS2Manager-staging-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $desktop ('HS2Manager-backup-' + [Guid]::NewGuid().ToString('N'))
foreach ($path in @($output, $staging, $backup)) {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -ne $desktop) {
        throw '发布目录超出指定桌面目录。'
    }
}

function Assert-DesktopInstallation {
    foreach ($path in @($output, (Join-Path $output 'Data'))) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "桌面程序和 Data 必须是普通文件夹：$path"
            }
        }
    }
}

function Remove-DesktopBuildDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    # 仅清理本次调用在实际桌面目录下生成的目录。
    if ([IO.Path]::GetDirectoryName($resolved) -ne $desktop -or $resolved -notin @($staging, $backup)) {
        throw '临时构建目录超出指定桌面目录。'
    }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "临时构建目录不能是链接：$resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Lock-DesktopLibrary {
    $dataPath = [IO.Path]::GetFullPath((Join-Path $output 'Data')).ToUpperInvariant()
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $key = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($dataPath)))).Replace('-', '').Substring(0, 24)
    }
    finally {
        $hash.Dispose()
    }
    $created = $false
    $mutex = [Threading.Mutex]::new($false, ('Local\HS2Manager.App.' + $key), [ref]$created)
    if (!$created) {
        $mutex.Dispose()
        throw '请先关闭桌面版 HS2 Manager，再替换程序，避免复制正在写入的资料库。'
    }
    return $mutex
}

function Move-InstallationEntry([string]$Source, [string]$Destination) {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    foreach ($path in @($sourcePath, $destinationPath)) {
        if ([IO.Path]::GetDirectoryName($path) -notin @($output, $staging, $backup)) {
            throw '安装文件移动路径越界。'
        }
    }
    $item = Get-Item -LiteralPath $sourcePath -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "安装文件不能是链接：$sourcePath"
    }
    if ($item.PSIsContainer) {
        [IO.Directory]::Move($sourcePath, $destinationPath)
    }
    else {
        [IO.File]::Move($sourcePath, $destinationPath)
    }
}

$libraryLock = $null
Assert-DesktopInstallation
try {
    # 完整构建便携版本成功后，再调整已安装版本。
    & (Join-Path $PSScriptRoot 'build.ps1') @PSBoundParameters `
        -OutputDirectory $staging -SkipArchive

    Assert-DesktopInstallation
    $libraryLock = Lock-DesktopLibrary
    if (Test-Path -LiteralPath $output) {
        New-Item -ItemType Directory -Path $backup | Out-Null
        $oldEntries = [Collections.Generic.List[string]]::new()
        $newEntries = [Collections.Generic.List[string]]::new()
        try {
            # 被崩溃快照映射的 SHM 会阻止重命名整个安装目录。
            # 保留 Data 及恢复备份的原路径，只事务式替换程序文件。
            foreach ($item in Get-ChildItem -LiteralPath $output -Force) {
                if ($item.Name -match '^Data(?:\.|$)') {
                    continue
                }
                Move-InstallationEntry $item.FullName (Join-Path $backup $item.Name)
                $oldEntries.Add($item.Name)
            }
            foreach ($item in Get-ChildItem -LiteralPath $staging -Force) {
                if ($item.Name -eq 'Data' -and (Test-Path -LiteralPath (Join-Path $output 'Data'))) {
                    continue
                }
                Move-InstallationEntry $item.FullName (Join-Path $output $item.Name)
                $newEntries.Add($item.Name)
            }
        }
        catch {
            # 回滚到刚才完整的旧程序；资料库始终保留在原位置。
            for ($index = $newEntries.Count - 1; $index -ge 0; $index--) {
                $name = $newEntries[$index]
                Move-InstallationEntry (Join-Path $output $name) (Join-Path $staging $name)
            }
            for ($index = $oldEntries.Count - 1; $index -ge 0; $index--) {
                $name = $oldEntries[$index]
                Move-InstallationEntry (Join-Path $backup $name) (Join-Path $output $name)
            }
            Remove-DesktopBuildDirectory $backup
            throw
        }
        try {
            Remove-DesktopBuildDirectory $backup
        }
        catch {
            Write-Warning "新版本已发布，旧版本清理失败，保留在：$backup。$($_.Exception.Message)"
        }
    }
    else {
        [IO.Directory]::Move($staging, $output)
    }
    Write-Output "桌面便携目录：$output"
}
finally {
    if ($libraryLock) {
        $libraryLock.Dispose()
    }
    Remove-DesktopBuildDirectory $staging
}
