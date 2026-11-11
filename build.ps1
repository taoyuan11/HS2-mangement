[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [switch]$SkipTests,
    [switch]$PreviewTest,
    [string]$PreviewCard = '',
    [string]$FbxSdkRoot = '',
    [string]$PhysXSource = '',
    [string]$OutputDirectory = '',
    [switch]$SkipArchive,
    [string]$BuildDirectory = '',
    [ValidateRange(1, 32)][int]$Jobs = [Math]::Min(4, [Environment]::ProcessorCount)
)
$ErrorActionPreference = 'Stop'
# 生产构建仅包含 C++ 应用模块和原生运行库。
& (Join-Path $PSScriptRoot 'scripts/package/Publish-Cpp.ps1') @PSBoundParameters
