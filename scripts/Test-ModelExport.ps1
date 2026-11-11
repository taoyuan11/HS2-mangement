[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$EditorPath='',[int]$TimeoutSeconds=600)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(!$EditorPath){$EditorPath=Join-Path $repo '.tools/unity/2018.4.11f1/Editor/Unity.exe'}
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
$validationProject=Join-Path $OutputDirectory ('unity-validation-project-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $validationProject 'Assets/Editor'),(Join-Path $validationProject 'ProjectSettings'),(Join-Path $validationProject 'Packages') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'tests/reference/src/HS2Manager.UnityPreview/Assets/Editor/ModelExportValidation.cs') -Destination (Join-Path $validationProject 'Assets/Editor/ModelExportValidation.cs')
Copy-Item -LiteralPath (Join-Path $repo 'tests/reference/src/HS2Manager.Preview.Contracts/ModelExport.cs') -Destination (Join-Path $validationProject 'Assets/ModelExport.cs')
Set-Content -LiteralPath (Join-Path $validationProject 'Packages/manifest.json') -Value '{"dependencies":{"com.unity.modules.animation":"1.0.0","com.unity.modules.imageconversion":"1.0.0"}}' -Encoding UTF8
$editorVersion=[regex]::Match($EditorPath,'[0-9]+\.[0-9]+\.[0-9]+[abfp][0-9]+').Value
if($editorVersion){Set-Content -LiteralPath (Join-Path $validationProject 'ProjectSettings/ProjectVersion.txt') -Value ('m_EditorVersion: '+$editorVersion) -Encoding UTF8}
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $repo 'scripts/OwnedToolProcess.cs')}
$previous=$env:HS2_MODEL_VALIDATION_ROOT
try {
    $env:HS2_MODEL_VALIDATION_ROOT=$OutputDirectory
    $log=Join-Path $OutputDirectory 'unity-import-validation.log'
    $code=[HS2Manager.BuildTools.OwnedToolProcess]::Run([IO.Path]::GetFullPath($EditorPath),@('-batchmode','-quit','-projectPath',$validationProject,'-executeMethod','HS2Manager.UnityPreview.Editor.ModelExportValidation.Run','-logFile',$log),$repo,$TimeoutSeconds,$log)
    $report=Join-Path $OutputDirectory 'unity-import-validation.json'
    if($code -ne 0 -or !(Test-Path -LiteralPath $report)){throw "Unity model import validation failed ($code). See $log"}
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $result | Select-Object UnityVersion,Passed,Characters,Meshes,Vertices,BlendShapes,MaxPositionError,Error | Format-List
}
finally {$env:HS2_MODEL_VALIDATION_ROOT=$previous}
