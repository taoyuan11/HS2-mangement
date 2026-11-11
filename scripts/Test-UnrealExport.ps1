[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$EditorPath='C:/Program Files/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe',
    [switch]$Render
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new output directory.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$project=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('HS2Manager/Tests/Unreal/'+[Guid]::NewGuid().ToString('N'))
$cache=Join-Path (Split-Path -Parent $project) 'DDC'
New-Item -ItemType Directory -Path (Join-Path $project 'Config') -Force | Out-Null
$uproject=Join-Path $project 'HS2Export.uproject'
Set-Content -LiteralPath $uproject -Encoding utf8 -Value '{"FileVersion":3,"Plugins":[{"Name":"PythonScriptPlugin","Enabled":true},{"Name":"EditorScriptingUtilities","Enabled":true}]}'
# Isolated test project only. Do not change the user's rendering settings.
Set-Content -LiteralPath (Join-Path $project 'Config/DefaultEngine.ini') -Encoding utf8 -Value "[/Script/Engine.RendererSettings]`nr.RayTracing=False`nr.AllowStaticLighting=False`n[Zen.AutoLaunch]`nAutoLaunch=False`n[DerivedDataBackendGraph]`nRoot=(Type=KeyLength, Length=120, Inner=Local)`nLocal=(Type=FileSystem, ReadOnly=false, Clean=false, Flush=false, Path=$cache)"
$config=Join-Path $OutputDirectory 'request.json'
@{Packages=@($PackageDirectory|ForEach-Object{(Resolve-Path -LiteralPath $_).Path});Importer=(Join-Path $repo 'tests/reference/src/HS2Manager.UnityPreview/Assets/Resources/UnrealImport.py.txt');Output=$OutputDirectory;Project=$uproject} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $repo 'scripts/OwnedToolProcess.cs')}
$previous=$env:HS2_UNREAL_VALIDATION
try {
    $env:HS2_UNREAL_VALIDATION=$config
    $log=Join-Path $OutputDirectory 'unreal.log'
    $test=Join-Path $repo 'tests/Test-UnrealExport.py'
    $code=[HS2Manager.BuildTools.OwnedToolProcess]::Run($EditorPath,@($uproject,'-run=pythonscript',('-script='+$test),'-unattended','-nop4','-nosplash','-nosound','-NoLiveCoding','-NoZenAutoLaunch','-notrace','-ddc=DerivedDataBackendGraph',('-abslog='+$log)),$project,900,$null)
    $report=Join-Path $OutputDirectory 'validation.json'
    if($code -ne 0 -or !(Test-Path -LiteralPath $report)){throw "Unreal checks failed ($code). See $log"}
    $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    if(!$result.Passed){throw "Unreal checks failed: $($result.Error)"}
    if($Render){
        $renderEditor=Join-Path (Split-Path -Parent $EditorPath) 'UnrealEditor.exe'
        $renderScript=Join-Path $repo 'tests/Test-UnrealRender.py'
        $renderLog=Join-Path $OutputDirectory 'unreal-render.log'
        $code=[HS2Manager.BuildTools.OwnedToolProcess]::Run($renderEditor,@($uproject,('-ExecCmds=py '+$renderScript),'-unattended','-nop4','-nosplash','-nosound','-NoLiveCoding','-NoZenAutoLaunch','-notrace','-windowed','-ResX=1000','-ResY=1000','-ddc=DerivedDataBackendGraph',('-abslog='+$renderLog)),$project,900,$null)
        $renderReport=Join-Path $OutputDirectory 'render-validation.json'
        if($code -ne 0 -or !(Test-Path -LiteralPath $renderReport)){throw "Unreal render failed ($code). See $renderLog"}
        $renderResult=Get-Content -LiteralPath $renderReport -Raw|ConvertFrom-Json
        if(!$renderResult.Passed){throw "Unreal render failed: $($renderResult.Error)"}
        foreach($png in $renderResult.Screenshots){if(!(Test-Path -LiteralPath $png) -or (Get-Item -LiteralPath $png).Length -lt 1000){throw "Missing render: $png"}}
        if(Select-String -LiteralPath $renderLog -Pattern 'Failed to compile Material' -Quiet){throw "Unreal material shader compilation failed. See $renderLog"}
    }
    Get-Content -LiteralPath $report
}
finally {$env:HS2_UNREAL_VALIDATION=$previous}
