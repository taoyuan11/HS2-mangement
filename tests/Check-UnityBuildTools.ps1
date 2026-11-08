$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])) { Add-Type -Path (Join-Path $projectRoot 'scripts\OwnedToolProcess.cs') }
$sandbox = Join-Path $projectRoot ('artifacts\owned-process-checks\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$shell = (Get-Command powershell.exe).Source
$childScript = Join-Path $sandbox 'child.ps1'
Set-Content -LiteralPath $childScript -Value @'
param([string]$Output)
$child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60') -WindowStyle Hidden -PassThru
$child.Id | Set-Content -LiteralPath $Output
Start-Sleep -Seconds 60
'@
$pidFile = Join-Path $sandbox 'child-pid.txt'
$timedOut = $false
try {
    [HS2Manager.BuildTools.OwnedToolProcess]::Run($shell, @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $childScript, '-Output', $pidFile), $sandbox, 5, $null) | Out-Null
}
catch {
    if ($_.Exception.ToString() -notmatch 'exceeded 5 seconds') { throw }
    $timedOut = $true
}
if (!$timedOut -or !(Test-Path -LiteralPath $pidFile)) { throw 'Timeout/process-tree fixture did not run.' }
$ownedChildId = [int](Get-Content -LiteralPath $pidFile)
$deadline = [DateTime]::UtcNow.AddSeconds(5)
while ((Get-Process -Id $ownedChildId -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
if (Get-Process -Id $ownedChildId -ErrorAction SilentlyContinue) { Stop-Process -Id $ownedChildId -Force; throw 'Owned child survived the timeout.' }
Write-Output 'PASS: suspended launch, job assignment, timeout and descendant cleanup'
$exit = [HS2Manager.BuildTools.OwnedToolProcess]::Run($shell, @('-NoProfile', '-NonInteractive', '-Command', 'exit 17'), $sandbox, 10, $null)
if ($exit -ne 17) { throw 'Process exit status was lost.' }
Write-Output 'PASS: nonzero exit propagation'
$licenseLog = Join-Path $sandbox 'license.log'
Set-Content -LiteralPath $licenseLog -Value 'BatchMode: Unity has not been activated with a valid License.'
$licenseDetected = $false
try { [HS2Manager.BuildTools.OwnedToolProcess]::Run($shell, @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 60'), $sandbox, 10, $licenseLog) | Out-Null }
catch { if ($_.Exception.ToString() -notmatch 'no valid activated license') { throw }; $licenseDetected = $true }
if (!$licenseDetected) { throw 'Unity license failure was not detected.' }
Write-Output 'PASS: license failure stops the owned editor without waiting for timeout'
