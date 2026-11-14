[CmdletBinding()]
param([string]$Directory = (Join-Path $PSScriptRoot '../../.tools/ci'))
$ErrorActionPreference = 'Stop'
$dependencies = & (Join-Path $PSScriptRoot 'Restore-NativeDependencies.ps1') -Directory $Directory
Write-Output "FBX SDK: $($dependencies.FbxSdkRoot)"
Write-Output "PhysX: $($dependencies.PhysXSource)"
