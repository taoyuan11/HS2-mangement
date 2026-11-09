[CmdletBinding()]
param(
    [string]$GamePath='Z:\games\hs2\HS two DX - TSYMQ',
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$PreviousManifest
)
$ErrorActionPreference='Stop'
$game=[IO.Path]::GetFullPath($GamePath)
$output=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $output){throw 'Use a new baseline output directory.'}
New-Item -ItemType Directory -Path $output | Out-Null
$files=@(foreach($folder in @('HoneySelect2_Data','BepInEx\core','BepInEx\patchers','BepInEx\plugins','BepInEx\config','DHH_Data','mods','abdata','UserData\chara','UserData\custom','UserData\config')){
    $path=Join-Path $game $folder
    if(Test-Path -LiteralPath $path){Get-ChildItem -LiteralPath $path -Recurse -File}
})+@(Get-Item -LiteralPath (Join-Path $game 'HoneySelect2.exe'))+@(Get-Item -LiteralPath (Join-Path $game 'UserData\setup.xml'))
$prior=@{}
if($PreviousManifest){foreach($line in Get-Content -LiteralPath $PreviousManifest){$entry=$line|ConvertFrom-Json;$prior[$entry.Path]=$entry}}
$writer=[IO.StreamWriter]::new((Join-Path $output 'files.jsonl'),$false,[Text.UTF8Encoding]::new($false))
try{
    $index=0
    foreach($file in ($files | Sort-Object FullName -Unique)){
        $before=$file.Length.ToString()+'|'+$file.LastWriteTimeUtc.Ticks
        $relative=[IO.Path]::GetRelativePath($game,$file.FullName)
        $previous=$prior[$relative]
        $hash=if($previous -and $previous.Length -eq $file.Length -and $previous.ModifiedUtcTicks -eq $file.LastWriteTimeUtc.Ticks){$previous.SHA256}else{(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
        $file.Refresh()
        if($before -ne ($file.Length.ToString()+'|'+$file.LastWriteTimeUtc.Ticks)){throw ('Baseline input changed while hashing: '+$file.FullName)}
        $writer.WriteLine((@{Path=$relative;Length=$file.Length;ModifiedUtcTicks=$file.LastWriteTimeUtc.Ticks;SHA256=$hash}|ConvertTo-Json -Compress))
        $index++
        if($index%200 -eq 0){$writer.Flush();Write-Output "Pinned $index/$($files.Count) files"}
    }
}finally{$writer.Dispose()}
@{Game=$game;Utc=[DateTime]::UtcNow.ToString('O');Files=$index;ManifestSHA256=(Get-FileHash -LiteralPath (Join-Path $output 'files.jsonl') -Algorithm SHA256).Hash;StaticViews=@('front','side','back','face');Frames=360;Hz=30;Samples=24;Seed=1979;SSIM=.99;MeanChannelError=2.0/255;LocalReviewRequired=$true}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'baseline.json') -Encoding utf8
Write-Output "Baseline pinned: $output"
