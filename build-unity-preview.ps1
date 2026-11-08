[CmdletBinding()]
param(
    [string]$EditorPath = '',
    [string]$OutputPath = '',
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 900,
    [string]$ProbeManifest = ''
)
$ErrorActionPreference = 'Stop'
if (!$EditorPath) { $EditorPath = Join-Path $PSScriptRoot '.tools\unity\2018.4.11f1\Editor\Unity.exe' }
if (!(Test-Path -LiteralPath $EditorPath)) { throw 'Unity 2018.4.11f1 is required. Run bootstrap-unity.ps1 or specify -EditorPath.' }
$EditorPath = [IO.Path]::GetFullPath($EditorPath)
if ((Get-Item -LiteralPath $EditorPath).VersionInfo.ProductVersion -ne '2018.4.11.7379119') { throw 'Only Unity 2018.4.11f1 is accepted for this validation.' }
if (!$OutputPath) { $OutputPath = Join-Path $PSScriptRoot ('artifacts\unity-preview\' + [Guid]::NewGuid().ToString('N')) }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw 'Use a new output directory so a failed build cannot reuse an old player or report.' }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$project = Join-Path $PSScriptRoot 'src\HS2Manager.UnityPreview'
$generated = Join-Path $project 'Assets\Generated\Contracts'
New-Item -ItemType Directory -Path $generated -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src\HS2Manager.Preview.Contracts') -Filter '*.cs' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $generated -Force }
Set-Content -LiteralPath (Join-Path $generated 'HS2Manager.Preview.Contracts.asmdef') -Value '{"name":"HS2Manager.Preview.Contracts","references":[],"includePlatforms":[],"excludePlatforms":[]}' -Encoding UTF8
if (!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'scripts\OwnedToolProcess.cs') }
$log = Join-Path $OutputPath 'build.log'
$previousOutput = $env:HS2_PREVIEW_BUILD_OUTPUT
try {
    $env:HS2_PREVIEW_BUILD_OUTPUT = $OutputPath
    $exitCode = [HS2Manager.BuildTools.OwnedToolProcess]::Run($EditorPath, @('-batchmode', '-quit', '-projectPath', $project, '-executeMethod', 'HS2Manager.UnityPreview.Editor.PreviewBuild.Build', '-logFile', $log), $PSScriptRoot, $TimeoutSeconds, $log)
    $executable = Join-Path $OutputPath 'HS2Manager.UnityPreview.exe'
    if ($exitCode -ne 0 -or !(Test-Path -LiteralPath $executable)) { throw "Unity build failed ($exitCode). See $log" }
    if ($ProbeManifest) {
        $ProbeManifest = (Resolve-Path -LiteralPath $ProbeManifest).Path
        $report = Join-Path $OutputPath 'resource-report.json'
        $exitCode = [HS2Manager.BuildTools.OwnedToolProcess]::Run($executable, @('-batchmode', '-force-d3d11', '-screen-fullscreen', '0', '--resource-probe', $ProbeManifest, '--report', $report, '-logFile', (Join-Path $OutputPath 'probe.log')), $OutputPath, 300, $null)
        if ($exitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw "Original resource validation failed ($exitCode). See $OutputPath" }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($result.ResourceCompatibilityPassed -ne $true -or $result.VisualParityVerified -ne $false) { throw 'Invalid resource probe result; visual parity must be verified separately.' }
        Write-Output "Resource probe passed: $report"
    }
    $marker=Join-Path $PSScriptRoot 'artifacts\unity-preview\current-player.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $marker) -Force | Out-Null
    Set-Content -LiteralPath $marker -Value $executable -Encoding utf8
    Write-Output "Independent Unity player built (visual acceptance is separate): $executable"
}
finally { $env:HS2_PREVIEW_BUILD_OUTPUT = $previousOutput }
