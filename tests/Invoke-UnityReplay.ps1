[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Catalog,
    [Parameter(Mandatory=$true)][string]$ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$PlayerDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [switch]$Follow,
    [ValidateRange(1,4)][int]$Workers=1,
    [ValidateRange(1,180)][int]$WaitMinutes=15
)
$ErrorActionPreference='Stop'
$entries=@(Get-Content -LiteralPath $Catalog -Raw|ConvertFrom-Json)
$output=[IO.Path]::GetFullPath($OutputPath)
$references=(Resolve-Path -LiteralPath $ReferenceRoot).Path
if(Test-Path -LiteralPath $output){throw 'Use a fresh replay directory.'}
New-Item -ItemType Directory -Path $output|Out-Null
Copy-Item -LiteralPath $Catalog -Destination (Join-Path $output 'catalog.json')
$player=(Resolve-Path -LiteralPath $PlayerDirectory).Path
$characterScript=Join-Path $PSScriptRoot 'Invoke-UnityCharacter.ps1'
$compareScript=Join-Path $PSScriptRoot 'Compare-UnityVisual.py'
$python=Join-Path $PSScriptRoot '..\.tools\image-compare\Scripts\python.exe'
if($Workers -gt 1){
    if($PSVersionTable.PSVersion.Major -lt 7){throw 'Parallel replay requires PowerShell 7.'}
    if(!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot '..\scripts\OwnedToolProcess.cs')}
    $results=@()
    $entries|ForEach-Object -Parallel {
        $ErrorActionPreference='Stop'
        $entry=$_;$reference=Join-Path $using:references ($entry.Id+'\reference');$directory=Join-Path $using:output $entry.Id
        $actual=Join-Path $directory 'independent';$comparison=Join-Path $directory 'comparison';$errorText=$null
        New-Item -ItemType Directory -Path $directory -Force|Out-Null
        try{
            $deadline=[DateTime]::UtcNow.AddMinutes($using:WaitMinutes)
            while(!(Test-Path -LiteralPath (Join-Path $reference 'finished.txt'))){
                if(!$using:Follow -or [DateTime]::UtcNow -ge $deadline){throw "Reference incomplete: $reference"}
                Start-Sleep -Seconds 5
            }
            if((Get-Content -LiteralPath (Join-Path $reference 'finished.txt') -Raw).Trim() -ne 'success'){throw "Reference failed: $reference"}
            if($entry.Error){throw $entry.Error}
            & $using:characterScript -PlayerDirectory $using:player -Definition $entry.Definition -OutputPath $actual -Fixture (Join-Path $reference 'fixture.json') -TimeoutSeconds 300 > (Join-Path $directory 'capture.log')
            & $using:python $using:compareScript $reference $actual $comparison > (Join-Path $directory 'comparison-summary.json')
            if($LASTEXITCODE -ne 0){throw 'Comparison failed.'}
        }catch{$errorText=$_.Exception.ToString()}
        $hash=(Get-FileHash -LiteralPath $entry.Card -Algorithm SHA256).Hash
        if($hash -ne $entry.Hash){throw "Original card changed: $($entry.Card)"}
        $result=@{Id=$entry.Id;Card=$entry.Card;HashUnchanged=$true;Reference=$reference;Independent=$actual;Comparison=$comparison;Error=$errorText}
        $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $directory 'result.json') -Encoding utf8
        $result
    } -ThrottleLimit $Workers|ForEach-Object {
        $results+=$_
        $results|Sort-Object {$_.Id}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
        Write-Output "Replay [$($results.Count)/$($entries.Count)] $($_.Id): $(if($_.Error){'FAILED'}else{'compared'})"
    }
    return
}
$results=@()
for($i=0;$i -lt $entries.Count;$i++){
    $entry=$entries[$i];$reference=Join-Path $references ($entry.Id+'\reference');$actual=Join-Path $output ($entry.Id+'\independent');$comparison=Join-Path $output ($entry.Id+'\comparison');$errorText=$null
    try{
        $deadline=[DateTime]::UtcNow.AddMinutes($WaitMinutes)
        while(!(Test-Path -LiteralPath (Join-Path $reference 'finished.txt'))){
            if(!$Follow -or [DateTime]::UtcNow -ge $deadline){throw "Reference incomplete: $reference"}
            Start-Sleep -Seconds 5
        }
        if((Get-Content -LiteralPath (Join-Path $reference 'finished.txt') -Raw).Trim() -ne 'success'){throw "Reference failed: $reference"}
        if($entry.Error){throw $entry.Error}
        & (Join-Path $PSScriptRoot 'Invoke-UnityCharacter.ps1') -PlayerDirectory $PlayerDirectory -Definition $entry.Definition -OutputPath $actual -Fixture (Join-Path $reference 'fixture.json') -TimeoutSeconds 300
        & (Join-Path $PSScriptRoot '..\.tools\image-compare\Scripts\python.exe') (Join-Path $PSScriptRoot 'Compare-UnityVisual.py') $reference $actual $comparison > (Join-Path $output ($entry.Id+'-summary.json'))
        if($LASTEXITCODE -ne 0){throw 'Comparison failed.'}
    }catch{$errorText=$_.Exception.ToString();Write-Output $errorText}
    $hash=(Get-FileHash -LiteralPath $entry.Card -Algorithm SHA256).Hash
    if($hash -ne $entry.Hash){throw "Original card changed: $($entry.Card)"}
    $results+=@{Id=$entry.Id;Card=$entry.Card;HashUnchanged=$true;Reference=$reference;Independent=$actual;Comparison=$comparison;Error=$errorText}
    $results|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    Write-Output "Replay [$($i+1)/$($entries.Count)] $($entry.Id): $(if($errorText){'FAILED'}else{'compared'})"
}
