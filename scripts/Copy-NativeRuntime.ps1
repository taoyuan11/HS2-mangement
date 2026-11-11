[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$CrtDirectory=''
)
$ErrorActionPreference='Stop'
$packagePath=[IO.Path]::GetFullPath($Package)
if(!(Test-Path -LiteralPath $packagePath -PathType Container)){throw 'The native package staging directory is missing.'}
$native=Join-Path $packagePath 'native/win-x64'
foreach($name in @('HS2Manager.Physics.dll','PhysX3_x64.dll','PhysX3Common_x64.dll','PxFoundation_x64.dll','PxPvdSDK_x64.dll','HS2Manager.FbxBridge.dll','libfbxsdk.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $native $name) -PathType Leaf)){throw ('Native runtime file is missing: '+$name+'. Build the independent native adapters before publishing.')}
}
if(!$CrtDirectory){
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if(Test-Path -LiteralPath $vswhere){
        $installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if($LASTEXITCODE -ne 0){throw 'Visual Studio C++ runtime discovery failed.'}
        if($installation){
            $root=Join-Path $installation 'VC/Redist/MSVC'
            $CrtDirectory=Get-ChildItem -LiteralPath $root -Directory | Where-Object {$_.Name -match '^\d+\.\d+\.\d+$'} |
                Sort-Object {[version]$_.Name} -Descending | ForEach-Object {Join-Path $_.FullName 'x64/Microsoft.VC143.CRT'} |
                Where-Object {Test-Path -LiteralPath $_ -PathType Container} | Select-Object -First 1
        }
    }
}
if(!$CrtDirectory){throw 'The app-local Microsoft VC143 runtime is missing. Specify -CrtDirectory from the licensed Visual Studio redist directory.'}
foreach($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
    $source=Join-Path ([IO.Path]::GetFullPath($CrtDirectory)) $name
    if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw ('Redistributable runtime file is missing: '+$source)}
    Copy-Item -LiteralPath $source -Destination (Join-Path $native $name)
}
Get-ChildItem -LiteralPath $native -File -Filter '*.dll' | ForEach-Object {
    $stream=[IO.File]::OpenRead($_.FullName);$algorithm=[Security.Cryptography.SHA256]::Create()
    try{$digest=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}
    finally{$algorithm.Dispose();$stream.Dispose()}
    [pscustomobject]@{File=$_.Name;SHA256=$digest;Bytes=$_.Length}
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packagePath 'native-runtime.json') -Encoding utf8
