[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Catalog,
    [string]$PlayerDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$GamePath='Z:\games\hs2\HS two DX - TSYMQ',
    [int]$Take=0,
    [switch]$ReferenceOnly,
    [switch]$Resume
)
$ErrorActionPreference='Stop'
$entries=@(Get-Content -LiteralPath $Catalog -Raw|ConvertFrom-Json)
if($Take -gt 0){$entries=@($entries|Select-Object -First $Take)}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$ReferenceOnly -and !$PlayerDirectory){throw 'PlayerDirectory is required unless ReferenceOnly is selected.'}
if((Test-Path -LiteralPath $output) -and !$Resume){throw 'Use a fresh catalog validation directory, or Resume a previous collection.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$results=if($Resume -and (Test-Path -LiteralPath (Join-Path $output 'results.json'))){@(Get-Content -LiteralPath (Join-Path $output 'results.json') -Raw|ConvertFrom-Json)}else{@()}
for($i=0;$i -lt $entries.Count;$i++){
    $entry=$entries[$i];$reference=Join-Path $output ($entry.Id+'\reference');$actual=Join-Path $output ($entry.Id+'\independent');$comparison=Join-Path $output ($entry.Id+'\comparison');$errorText=$null
    $finished=Join-Path $reference 'finished.txt'
    $referenceComplete=(Test-Path -LiteralPath $finished) -and (Get-Content -LiteralPath $finished -Raw).Trim() -eq 'success'
    $existing=@($results|Where-Object {$_.Id -eq $entry.Id})
    if($Resume -and $referenceComplete -and $existing.Count -and ($ReferenceOnly -or !$existing[0].Error)){continue}
    try{
        # Every reference uses a fresh game process. Plugins retain state across maker reloads.
        if(!$referenceComplete){& (Join-Path $PSScriptRoot 'Invoke-GameReference.ps1') -GamePath $GamePath -CardPath $entry.Card -OutputPath $reference -Maker -TimeoutSeconds 600}
        if(!$ReferenceOnly){
            & (Join-Path $PSScriptRoot 'Invoke-UnityCharacter.ps1') -PlayerDirectory $PlayerDirectory -Definition $entry.Definition -OutputPath $actual -Fixture (Join-Path $reference 'fixture.json') -TimeoutSeconds 300
            & (Join-Path $PSScriptRoot '..\.tools\image-compare\Scripts\python.exe') (Join-Path $PSScriptRoot 'Compare-UnityVisual.py') $reference $actual $comparison > (Join-Path $output ($entry.Id+'-summary.json'))
            if($LASTEXITCODE -ne 0){throw 'Comparison failed.'}
        }
    }catch{$errorText=$_.Exception.ToString();Write-Output $errorText}
    $hash=(Get-FileHash -LiteralPath $entry.Card -Algorithm SHA256).Hash
    if($hash -ne $entry.Hash){throw "Original card changed: $($entry.Card)"}
    $result=@{Id=$entry.Id;Card=$entry.Card;HashUnchanged=$true;Reference=$reference;Independent=$actual;Comparison=$comparison;Error=$errorText}
    $results=@($results|Where-Object {$_.Id -ne $entry.Id})+@($result)
    $results|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    Write-Output "[$($i+1)/$($entries.Count)] $($entry.Id): $(if($errorText){'FAILED'}elseif($ReferenceOnly){'reference captured'}else{'captured; see comparison'})"
}
