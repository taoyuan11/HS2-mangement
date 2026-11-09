[CmdletBinding()]
param(
    [string]$GamePath = 'Z:\games\hs2\HS two DX - TSYMQ',
    [string]$CardPath,
    [string]$Catalog,
    [int]$Take=0,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateRange(1,14400)][int]$TimeoutSeconds=180,
    [switch]$Maker
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game=[IO.Path]::GetFullPath($GamePath)
$executable=Join-Path $game 'HoneySelect2.exe'
$entries=if($Catalog){@(Get-Content -LiteralPath $Catalog -Raw|ConvertFrom-Json|Where-Object {$_.Sex -eq 1})}else{@()}
if($Take -gt 0){$entries=@($entries|Select-Object -First $Take)}
if($entries.Count){$CardPath=$entries[0].Card;$Maker=$true}
$card=(Resolve-Path -LiteralPath $CardPath).Path
$output=[IO.Path]::GetFullPath($OutputPath)
$stopRequest=Join-Path (Split-Path (Split-Path $output -Parent) -Parent) 'stop-requested.txt'
if(Test-Path -LiteralPath $stopRequest){throw [OperationCanceledException]::new('Reference batch stopped at a completed-card boundary.')}
if(Test-Path -LiteralPath $output){throw 'Use a new output directory for each reference capture.'}
$collector=Join-Path $PSScriptRoot 'HS2Manager.GameReference\bin\Debug\net46\HS2Manager.GameReference.dll'
if(!(Test-Path -LiteralPath $collector)){throw 'Build the test reference collector first.'}
if(Get-Process -Name HoneySelect2 -ErrorAction SilentlyContinue){throw 'A user game process is running. Reference collection will not interfere with it.'}
$pluginDirectory=Join-Path $game ('BepInEx\plugins\HS2Manager-Reference-Test-'+[Guid]::NewGuid().ToString('N'))
$plugin=Join-Path $pluginDirectory 'HS2Manager.GameReference.dll'
$sourceHash=(Get-FileHash -LiteralPath $collector -Algorithm SHA256).Hash
$cardHash=(Get-FileHash -LiteralPath $card -Algorithm SHA256).Hash
$oldCard=$env:HS2_REFERENCE_CARD
$oldOutput=$env:HS2_REFERENCE_OUTPUT
$oldMaker=$env:HS2_REFERENCE_MAKER
$oldManifest=$env:HS2_REFERENCE_MANIFEST
$hashes=@{};foreach($entry in $entries){$hashes[$entry.Card]=(Get-FileHash -LiteralPath $entry.Card -Algorithm SHA256).Hash}
$settingsPath=Join-Path $game 'UserData\custom\customscene.dat'
$settingsBytes=if(Test-Path -LiteralPath $settingsPath){[IO.File]::ReadAllBytes($settingsPath)}else{$null}
$autosaveConfig=Join-Path $game 'BepInEx\config\com.deathweasel.bepinex.autosave.cfg'
$autosaveBytes=if(Test-Path -LiteralPath $autosaveConfig){[IO.File]::ReadAllBytes($autosaveConfig)}else{$null}
New-Item -ItemType Directory -Path $output | Out-Null
if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $workspace 'scripts\OwnedToolProcess.cs')}
try {
    if($autosaveBytes){$text=[Text.Encoding]::UTF8.GetString($autosaveBytes);[IO.File]::WriteAllText($autosaveConfig,($text -replace '(?m)^Autosave Enabled = true\s*$','Autosave Enabled = false'),[Text.UTF8Encoding]::new($false))}
    New-Item -ItemType Directory -Path $pluginDirectory | Out-Null
    Copy-Item -LiteralPath $collector -Destination $plugin
    $env:HS2_REFERENCE_CARD=$card
    $env:HS2_REFERENCE_OUTPUT=$output
    $env:HS2_REFERENCE_MAKER=if($Maker){'1'}else{'0'}
    if($entries.Count){$manifest=Join-Path $output 'manifest.json';@($entries|ForEach-Object {@{Card=$_.Card;Output=(Join-Path $output $_.Id)}})|ConvertTo-Json|Set-Content -LiteralPath $manifest -Encoding utf8;$env:HS2_REFERENCE_MANIFEST=$manifest}
    $arguments=@('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $output 'game.log'))
    if($env:HS2_RENDERDOC_DLL){$arguments+=@('--doorstop-target',$collector)}
    $exitCode=[HS2Manager.BuildTools.OwnedToolProcess]::Run($executable,$arguments,$game,$TimeoutSeconds,$null,(Join-Path $output 'finished.txt'))
    if($exitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $output 'complete.txt'))){throw "Reference collection failed ($exitCode). See $output"}
    Write-Output "Reference fixture captured: $output"
}
catch {
    if(!(Test-Path -LiteralPath (Join-Path $output 'finished.txt'))){
        [IO.File]::WriteAllText((Join-Path $output 'error.txt'),$_.Exception.ToString())
        [IO.File]::WriteAllText((Join-Path $output 'finished.txt'),'failure')
    }
    throw
}
finally {
    $env:HS2_REFERENCE_CARD=$oldCard
    $env:HS2_REFERENCE_OUTPUT=$oldOutput
    $env:HS2_REFERENCE_MAKER=$oldMaker
    $env:HS2_REFERENCE_MANIFEST=$oldManifest
    if($autosaveBytes){[IO.File]::WriteAllBytes($autosaveConfig,$autosaveBytes)}
    if($Maker -and $settingsBytes){[IO.File]::WriteAllBytes($settingsPath,$settingsBytes)}
    elseif($Maker -and (Test-Path -LiteralPath $settingsPath)){Remove-Item -LiteralPath $settingsPath}
    if(Test-Path -LiteralPath $plugin){
        if((Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash -ne $sourceHash){throw 'Temporary collector changed; retained for inspection.'}
        Remove-Item -LiteralPath $plugin
    }
    if((Test-Path -LiteralPath $pluginDirectory) -and !(Get-ChildItem -LiteralPath $pluginDirectory -Force)){Remove-Item -LiteralPath $pluginDirectory}
    if((Get-FileHash -LiteralPath $card -Algorithm SHA256).Hash -ne $cardHash){throw 'Reference card was unexpectedly modified.'}
    foreach($path in $hashes.Keys){if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$path]){throw "Reference card was unexpectedly modified: $path"}}
}
