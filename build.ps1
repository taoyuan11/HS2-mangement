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
    [string]$BuildDirectory = ''
)
$ErrorActionPreference = 'Stop'
# The production build contains only C++ application modules and native runtimes.
& (Join-Path $PSScriptRoot 'scripts/Publish-Cpp.ps1') @PSBoundParameters