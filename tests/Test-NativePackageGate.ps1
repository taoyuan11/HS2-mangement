$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
. (Join-Path $workspace 'scripts/LocalWorkspace.ps1')
$dotnet=Join-Path $workspace '.tools/dotnet/dotnet.exe'
$fastDotnet=Join-Path $env:LOCALAPPDATA 'HS2Manager/BuildTools/dotnet-10.0.403/dotnet.exe'
if(Test-Path -LiteralPath $fastDotnet){$dotnet=$fastDotnet}
$buildArguments=@(Get-Hs2BuildArguments $workspace 'NativePackageChecks')
& $dotnet build (Join-Path $PSScriptRoot 'HS2Manager.PackageChecks') -c NativePackageChecks --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false -m:1 @buildArguments --nologo
if($LASTEXITCODE -ne 0){throw 'Audit build failed.'}
$binary=if($buildArguments.Count){Join-Path $buildArguments[1] 'bin/HS2Manager.PackageChecks/nativepackagechecks/HS2Manager.PackageChecks.dll'}else{Join-Path $PSScriptRoot 'HS2Manager.PackageChecks/bin/NativePackageChecks/net10.0/HS2Manager.PackageChecks.dll'}
$fixture=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('HS2Manager/NativeChecks/package-gate-'+[Guid]::NewGuid().ToString('N'))
$clean=Join-Path $fixture 'clean';$unsafe=Join-Path $fixture 'forbidden'
New-Item -ItemType Directory -Path $clean,$unsafe | Out-Null
$sdk=Get-ChildItem -LiteralPath (Join-Path (Split-Path $dotnet) 'sdk') -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$compiler=Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll'
$runtime=Get-ChildItem -LiteralPath (Join-Path (Split-Path $dotnet) 'shared/Microsoft.NETCore.App') -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$core=Join-Path $runtime.FullName 'System.Private.CoreLib.dll'
function Compile-Fixture([string]$Name,[string]$Source,[string[]]$References=@()) {
    $sourcePath=Join-Path $fixture ($Name+'.cs');Set-Content -LiteralPath $sourcePath -Value $Source -Encoding utf8
    & $dotnet $compiler /noconfig /nostdlib+ /target:library "/reference:$core" "/out:$fixture/$Name.dll" @References $sourcePath
    if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed.'}
}
Compile-Fixture 'UnityEngine.CoreModule' 'namespace UnityEngine { public sealed class Marker {} }'
Compile-Fixture 'Consumer' 'public static class Consumer { public static UnityEngine.Marker Get() { return new UnityEngine.Marker(); } }' @('/reference:'+(Join-Path $fixture 'UnityEngine.CoreModule.dll'))
Compile-Fixture 'NativeConsumer' 'public static class NativeConsumer { [System.Runtime.InteropServices.DllImport("UnityPlayer.dll")] public static extern void Probe(); }'
Compile-Fixture 'MonoConsumer' 'public static class MonoConsumer { [System.Runtime.InteropServices.DllImport("mono-2.0-bdwgc.dll")] public static extern void Probe(); }'
Compile-Fixture 'LegacyEndpoint' 'namespace HS2Manager.Core { public sealed class UnityPreviewSession {} }'
Compile-Fixture 'WinRT.Runtime' '[assembly:System.Reflection.AssemblyVersion("2.2.0.0")] namespace WinRT { internal static class Mono { [System.Runtime.InteropServices.DllImport("mono-2.0-bdwgc.dll")] public static extern void Probe(); } }'
Compile-Fixture 'Clean' 'public static class Clean { public static int Value() { return 42; } }'
Copy-Item -LiteralPath (Join-Path $fixture 'Clean.dll') -Destination (Join-Path $clean 'clean.data')
Copy-Item -LiteralPath (Join-Path $fixture 'UnityEngine.CoreModule.dll') -Destination (Join-Path $unsafe 'disguised.data')
Copy-Item -LiteralPath (Join-Path $fixture 'Consumer.dll'),(Join-Path $fixture 'NativeConsumer.dll'),(Join-Path $fixture 'MonoConsumer.dll'),(Join-Path $fixture 'LegacyEndpoint.dll'),(Join-Path $fixture 'WinRT.Runtime.dll') -Destination $unsafe
& $dotnet $binary $clean (Join-Path $fixture 'clean.json')
if($LASTEXITCODE -ne 0){throw 'Clean renamed assembly was incorrectly rejected.'}
& $dotnet $binary $unsafe (Join-Path $fixture 'forbidden.json')
if($LASTEXITCODE -eq 0){throw 'Forbidden assemblies were accepted.'}
$report=Get-Content -LiteralPath (Join-Path $fixture 'forbidden.json') -Raw | ConvertFrom-Json
foreach($kind in @('AssemblyIdentity','AssemblyReference','PlatformInvoke','LegacyRuntimeEndpoint')) {
    if(!($report.Failures | Where-Object Kind -eq $kind)){throw "Missing rejection: $kind"}
}
if(!($report.Failures | Where-Object Dependency -eq 'mono-2.0-bdwgc.dll')){throw 'Managed Mono runtime import was accepted.'}
if(!($report.Failures | Where-Object {$_.File -eq 'WinRT.Runtime.dll' -and $_.Kind -eq 'PlatformInvoke'})){throw 'A spoofed Microsoft runtime bypassed the binary fingerprint.'}
Write-Output "PASS native package dependency audit: renamed PE identity, managed references, native P/Invoke, and clean package. Evidence: $fixture"
exit 0
