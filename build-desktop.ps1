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

Assert-DesktopInstallation
try {
    # 完整构建便携版本成功后，再调整已安装版本。
    & (Join-Path $PSScriptRoot 'build.ps1') @PSBoundParameters `
        -OutputDirectory $staging -SkipArchive

    Assert-DesktopInstallation
    if (Test-Path -LiteralPath $output) {
        # 所有重命名目标均为桌面下的同级目录；
        # Directory.Move 在目标已存在时拒绝操作，避免将安装目录嵌入另一安装目录。
        [IO.Directory]::Move($output, $backup)
        try {
            $oldData = Join-Path $backup 'Data'
            if (Test-Path -LiteralPath $oldData) {
                # 新构建的数据目录仅包含自动生成的 Data/README.txt。
                $newData = [IO.Path]::GetFullPath((Join-Path $staging 'Data'))
                if ([IO.Path]::GetDirectoryName($newData) -ne $staging) {
                    throw '数据目录超出临时构建目录。'
                }
                Remove-Item -LiteralPath $newData -Recurse -Force
                Copy-Item -LiteralPath $oldData -Destination $staging -Recurse -Force
            }
            [IO.Directory]::Move($staging, $output)
        }
        catch {
            # 替换版本确认可用前，保留完整的原安装目录。
            [IO.Directory]::Move($backup, $output)
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
    Remove-DesktopBuildDirectory $staging
}
