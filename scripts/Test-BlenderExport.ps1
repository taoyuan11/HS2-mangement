[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$BlenderPath,
    [ValidateRange(30,3600)][int]$TimeoutSeconds=600
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$PackageDirectory=(Resolve-Path -LiteralPath $PackageDirectory).Path
$BlenderPath=(Resolve-Path -LiteralPath $BlenderPath).Path
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$report=Join-Path $OutputDirectory 'validation.json'
if(Test-Path -LiteralPath $report){throw 'Use a new output directory for Blender validation.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$importer=Join-Path $PackageDirectory 'Blender导入.py'
if(!(Test-Path -LiteralPath $importer)){$importer=Join-Path $repo 'src/HS2Manager.NativeExport/Resources/ModelExport/BlenderImport.py'}
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $repo 'scripts/OwnedToolProcess.cs')}
$arguments=@('--background','--factory-startup','--python-exit-code','1','--log-file',(Join-Path $OutputDirectory 'blender.log'),'--python',(Join-Path $repo 'tests/Test-BlenderExport.py'),'--',$PackageDirectory,$OutputDirectory,$importer)
$code=[HS2Manager.BuildTools.OwnedToolProcess]::Run($BlenderPath,$arguments,$repo,$TimeoutSeconds,$null)
if($code -ne 0 -or !(Test-Path -LiteralPath $report)){throw "Blender validation failed ($code): $OutputDirectory"}
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if(!$result.Passed){throw 'Blender material checks failed.'}
$result | Format-List
