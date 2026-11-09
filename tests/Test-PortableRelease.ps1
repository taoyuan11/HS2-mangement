param([Parameter(Mandatory=$true)][string]$Package,[string]$Definition='')
$ErrorActionPreference='Stop'
$resolvedPackage=[IO.Path]::GetFullPath($Package)
$artifacts=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$releasePrefix=[IO.Path]::GetFullPath((Join-Path $artifacts 'release')).TrimEnd('\')+'\'
if(!$resolvedPackage.StartsWith($releasePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected release path'}
$zip=$resolvedPackage+'.zip'
$expected=(Get-Content -LiteralPath ($zip+'.sha256')).Split(' ')[0]
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected){throw 'Archive checksum mismatch'}
$contents=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    if($contents.Entries.FullName -match '(?i)(Assembly-CSharp|BepInEx|HS2Manager\.(Bridge|GameReference)|\.mesh\.br|\.zipmod|\.unity3d|scene\.json|library\.db)'){throw 'Unexpected game/test content in package'}
    foreach($required in 'HS2Manager.exe','AssetsTools.NET.dll','AssetsTools.NET.Texture.dll','AssetRipper.TextureDecoder.dll','licenses/AssetRipper.TextureDecoder-LICENSE.txt','UnityPreview/HS2Manager.UnityPreview.exe','UnityPreview/UnityPlayer.dll','UnityPreview/HS2Manager.UnityPreview_Data/Managed/HS2Manager.UnityPreview.dll','UnityPreview/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'){
        if(!($contents.Entries | Where-Object {$_.FullName.Replace('\','/').EndsWith('/'+$required)})){throw "Package missing $required"}
    }
}finally{$contents.Dispose()}
$smoke=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('HS2Manager\Tests\portable-smoke-'+[Guid]::NewGuid().ToString('N'))
$unpacked=Join-Path $smoke 'relocated package'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$unpacked)
$relocated=Join-Path $unpacked ([IO.Path]::GetFileName($resolvedPackage))
$data=Join-Path $smoke 'Data'
New-Item -ItemType Directory -Path $data | Out-Null
New-Item -ItemType Directory -Path (Join-Path $data 'NoGame/UserData/chara'),(Join-Path $data 'NoGame/mods') -Force | Out-Null
@{GamePath=(Join-Path $data 'NoGame')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
$process=$null
try{
    $process=Start-Process -FilePath (Join-Path $relocated 'HS2Manager.exe') -ArgumentList @('--data',('"'+$data+'"')) -WorkingDirectory $smoke -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath (Join-Path $data 'owned-process.txt') -Value $process.Id
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{
        $process.Refresh()
        if($process.HasExited){throw "Portable application exited before opening: $($process.ExitCode)"}
        if($process.MainWindowHandle -ne 0){break}
        Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $deadline)
    if($process.MainWindowHandle -eq 0){throw 'Portable application did not open a window'}
    if(!$process.CloseMainWindow()){throw 'Could not close portable application'}
    if(!$process.WaitForExit(20000)){throw 'Portable application did not shut down'}
    if($process.ExitCode -ne 0){throw "Portable application exit code $($process.ExitCode)"}
    $errors=Get-ChildItem -LiteralPath (Join-Path $data 'Logs') -File -ErrorAction SilentlyContinue | Where-Object {($_.Name -eq 'startup.log' -and $_.Length -gt 0) -or ($_.Name -eq 'application.log' -and (Get-Content -LiteralPath $_.FullName -Raw) -match 'System\.[\w.]+Exception|\s+at HS2Manager')}
    if($errors){throw 'Portable application logged an exception'}
    if($Definition){& (Join-Path $PSScriptRoot 'Invoke-UnityCharacter.ps1') -PlayerDirectory (Join-Path $relocated 'UnityPreview') -Definition $Definition -OutputPath (Join-Path $smoke 'independent')}
    @{Package=$zip;SHA256=$expected;Relocated=$relocated;AppExitCode=$process.ExitCode;AppProcessStopped=$process.HasExited;IndependentChecked=[bool]$Definition}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $smoke 'result.json') -Encoding utf8
    Write-Output "PASS portable archive checksum, dependencies, clean package, application startup and normal exit; owned PID $($process.Id) stopped"
}finally{
    if($process){if(!$process.HasExited){Stop-Process -Id $process.Id -Force;$process.WaitForExit()};$process.Dispose()}
}
