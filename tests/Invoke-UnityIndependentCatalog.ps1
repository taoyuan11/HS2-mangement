[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Catalog,
    [Parameter(Mandatory=$true)][string]$ReferenceRoot,
    [Parameter(Mandatory=$true)][string]$PlayerDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [int]$Take=0,
    [switch]$WaitForReferences,
    [switch]$Resume
)
$ErrorActionPreference='Stop'
$entries=@(Get-Content -LiteralPath $Catalog -Raw|ConvertFrom-Json)
if($Take -gt 0){$entries=@($entries|Select-Object -First $Take)}
$output=[IO.Path]::GetFullPath($OutputPath)
if((Test-Path -LiteralPath $output) -and !$Resume){throw 'Use a fresh output directory or Resume.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
Copy-Item -LiteralPath $Catalog -Destination (Join-Path $output 'catalog.json') -Force
$results=if($Resume -and (Test-Path -LiteralPath (Join-Path $output 'results.json'))){@(Get-Content -LiteralPath (Join-Path $output 'results.json') -Raw|ConvertFrom-Json)}else{@()}
foreach($entry in $entries){
    if(Test-Path -LiteralPath (Join-Path $output 'stop-requested.txt')){break}
    $reference=if($entry.Reference){[IO.Path]::GetFullPath($entry.Reference)}else{Join-Path ([IO.Path]::GetFullPath($ReferenceRoot)) ($entry.Id+'\reference')}
    $actual=Join-Path $output ($entry.Id+'\independent')
    $comparison=Join-Path $output ($entry.Id+'\comparison')
    if($Resume -and (Test-Path -LiteralPath (Join-Path $comparison 'report.json'))){continue}
    $finished=Join-Path $reference 'finished.txt'
    $deadline=[DateTime]::UtcNow.AddMinutes(15)
    while($WaitForReferences -and !(Test-Path -LiteralPath $finished) -and [DateTime]::UtcNow -lt $deadline){
        if(Test-Path -LiteralPath (Join-Path ([IO.Path]::GetFullPath($ReferenceRoot)) 'stop-requested.txt')){throw 'Reference collection stopped.'}
        Start-Sleep -Seconds 5
    }
    $failure=$null
    try{
        if(!(Test-Path -LiteralPath $finished) -or (Get-Content -LiteralPath $finished -Raw).Trim() -ne 'success'){throw 'Successful reference is missing.'}
        $fixture=Join-Path $reference 'fixture.json'
        $settings=Get-Content -LiteralPath $fixture -Raw|ConvertFrom-Json
        if($settings.TemporalPhaseVersion -lt 2 -or $settings.PoseSamplingVersion -lt 2 -or $settings.FaceTickVersion -lt 1 -or $settings.PhysicsResetVersion -lt 3){throw 'Reference lacks deterministic temporal phase, Update-phase animation sampling, pre-skinning face updates, or physics reset after the maker corrective pose has settled.'}
        & (Join-Path $PSScriptRoot 'Invoke-UnityCharacter.ps1') -PlayerDirectory $PlayerDirectory -Definition $entry.Definition -OutputPath $actual -Fixture $fixture -TimeoutSeconds 300
        & (Join-Path $PSScriptRoot '..\.tools\image-compare\Scripts\python.exe') (Join-Path $PSScriptRoot 'Compare-UnityVisual.py') $reference $actual $comparison > (Join-Path $output ($entry.Id+'-summary.json'))
        if($LASTEXITCODE -ne 0){throw 'Image comparison failed.'}
    }catch{$failure=$_.Exception.ToString();Write-Output $failure}
    if((Get-FileHash -LiteralPath $entry.Card -Algorithm SHA256).Hash -ne $entry.Hash){throw ('Pinned card changed: '+$entry.Card)}
    $result=@{Id=$entry.Id;Card=$entry.Card;HashUnchanged=$true;Reference=$reference;Independent=$actual;Comparison=$comparison;Error=$failure}
    $results=@($results|Where-Object {$_.Id -ne $entry.Id})+@($result)
    $results|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
    if(!$failure){$report=Get-Content -LiteralPath (Join-Path $comparison 'report.json') -Raw|ConvertFrom-Json;Write-Output "$($entry.Id): static=$($report.numericPass), dynamic=$($report.dynamicNumericPass); visual review required"}
}
