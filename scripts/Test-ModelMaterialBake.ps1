[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$EditorPath='')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(!$EditorPath){$EditorPath=Join-Path $repo '.tools/unity/2018.4.11f1/Editor/Unity.exe'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$project=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('HS2Manager/Tests/MaterialBake/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $project 'Assets/Editor'),(Join-Path $project 'Packages') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'src/HS2Manager.UnityPreview/Assets/Resources/ModelExportBake.shader') -Destination (Join-Path $project 'Assets/ModelExportBake.shader')
Copy-Item -LiteralPath (Join-Path $repo 'src/HS2Manager.UnityPreview/Assets/Editor/ModelMaterialBakeValidation.cs') -Destination (Join-Path $project 'Assets/Editor/ModelMaterialBakeValidation.cs')
Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Value '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0"}}' -Encoding utf8
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $repo 'scripts/OwnedToolProcess.cs')}
$previous=$env:HS2_BAKE_VALIDATION_REPORT
try {
    $report=Join-Path $OutputDirectory 'material-bake-validation.json'
    if(Test-Path -LiteralPath $report){throw 'Use a new output directory for material validation.'}
    $env:HS2_BAKE_VALIDATION_REPORT=$report
    $log=Join-Path $OutputDirectory 'unity.log'
    $code=[HS2Manager.BuildTools.OwnedToolProcess]::Run([IO.Path]::GetFullPath($EditorPath),@('-batchmode','-quit','-projectPath',$project,'-executeMethod','HS2Manager.UnityPreview.Editor.ModelMaterialBakeValidation.Run','-logFile',$log),$repo,300,$log)
    if($code -ne 0 -or !(Test-Path -LiteralPath $report)){throw "Material bake checks failed ($code). See $log"}
    Get-Content -LiteralPath $report
}
finally {$env:HS2_BAKE_VALIDATION_REPORT=$previous}
