[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PlayerDirectory,
    [string]$Definition='artifacts/checks/unity-character.json',
    [string]$OutputPath='',
    [string]$Fixture='',
    [string]$PrimeDefinition='',
    [ValidateRange(1,900)][int]$TimeoutSeconds=180,
    [switch]$ProbeAA,
    [switch]$CheckEnvironmentControls
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$player=(Resolve-Path -LiteralPath $PlayerDirectory).Path
$definitionPath=(Resolve-Path -LiteralPath $Definition).Path
if(!$OutputPath){$OutputPath=Join-Path $player 'character-check'}
$output=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory for each validation.'}
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $workspace 'scripts\OwnedToolProcess.cs')}
$arguments=@('-force-d3d11','-screen-fullscreen','0','--character',$definitionPath,'--output',$output,'-logFile',($output+'.log'))
if($Fixture){$arguments+=@('--fixture',(Resolve-Path -LiteralPath $Fixture).Path)}
if($PrimeDefinition){$arguments+=@('--prime-character',(Resolve-Path -LiteralPath $PrimeDefinition).Path)}
if($ProbeAA){$arguments+='--probe-aa'}
if($CheckEnvironmentControls){$arguments+='--check-environment-controls'}
$launch=Join-Path $player 'HS2Manager.UnityPreview.exe'
if($env:HS2_RENDERDOC){$arguments=@('capture','-w','-d',$player,'-c',(Join-Path $output 'graphics'),$launch)+$arguments;$launch=(Resolve-Path -LiteralPath $env:HS2_RENDERDOC).Path}
$exitCode=[HS2Manager.BuildTools.OwnedToolProcess]::Run($launch,$arguments,$player,$TimeoutSeconds,$null)
if($exitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $output 'state.json'))){throw "Independent character validation failed ($exitCode): $output"}
$state=Get-Content -LiteralPath (Join-Path $output 'state.json') -Raw|ConvertFrom-Json
if($state.Renderers.Count -eq 0){throw 'Empty independent renderer snapshot.'}
if($state.Assemblies|Where-Object {$_ -match '^(Assembly-CSharp|IL|BepInEx|HS2_)'}){throw 'Game/plugin CLR assembly was loaded in the independent renderer.'}
$definitionData=Get-Content -LiteralPath $definitionPath -Raw|ConvertFrom-Json
$resources=@($definitionData.BundlePaths|ForEach-Object {$file=Get-Item -LiteralPath $_;[ordered]@{Path=$file.FullName;Length=$file.Length;LastWriteTimeUtc=$file.LastWriteTimeUtc.ToString('o')}})
[ordered]@{CapturedAtUtc=[DateTime]::UtcNow.ToString('o');Engine='2018.4.11f1';Graphics='Direct3D11';Definition=$definitionPath;DefinitionSHA256=(Get-FileHash -LiteralPath $definitionPath -Algorithm SHA256).Hash;CardSHA256=$definitionData.CardHash;Player=$player;KernelSHA256=(Get-FileHash -LiteralPath (Join-Path $player 'HS2Manager.UnityPreview_Data\Managed\HS2Manager.UnityPreview.dll') -Algorithm SHA256).Hash;GameOrPluginAssemblyLoaded=$false;Resources=$resources}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $output 'diagnostics.txt')
Write-Output "Independent diagnostic capture: $output"
