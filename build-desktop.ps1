[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard = '',
    [string]$UnityPlayerDirectory = ''
)
$ErrorActionPreference = 'Stop'
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
if (!$desktop -or !(Test-Path -LiteralPath $desktop -PathType Container)) {
    throw '无法找到当前用户的桌面目录。'
}

# Share the portable release pipeline, including runtimes and the Unity player.
& (Join-Path $PSScriptRoot 'build.ps1') @PSBoundParameters `
    -OutputDirectory (Join-Path $desktop 'HS2Manager') -SkipArchive
