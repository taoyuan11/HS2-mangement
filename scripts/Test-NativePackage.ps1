[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Package,[string]$Report='', [switch]$LoadNative)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'LocalWorkspace.ps1')
$configuration='NativePackageChecks'
$buildArguments=@(Get-Hs2BuildArguments $workspace $configuration)
$dotnet=Join-Path $workspace '.tools/dotnet/dotnet.exe'
$fastDotnet=Join-Path $env:LOCALAPPDATA 'HS2Manager/BuildTools/dotnet-10.0.403/dotnet.exe'
if(Test-Path -LiteralPath $fastDotnet){$dotnet=$fastDotnet}
if(!(Test-Path -LiteralPath $dotnet)){$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
$project=Join-Path $workspace 'tests/HS2Manager.PackageChecks/HS2Manager.PackageChecks.csproj'
& $dotnet build $project -c $configuration --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false -m:1 @buildArguments --nologo
if($LASTEXITCODE -ne 0){throw 'Native package audit could not be built.'}
$binary=if($buildArguments.Count){Join-Path $buildArguments[1] 'bin/HS2Manager.PackageChecks/nativepackagechecks/HS2Manager.PackageChecks.dll'}else{Join-Path $workspace 'tests/HS2Manager.PackageChecks/bin/NativePackageChecks/net10.0/HS2Manager.PackageChecks.dll'}
$auditArguments=@([IO.Path]::GetFullPath($Package))
if($Report){$auditArguments+=[IO.Path]::GetFullPath($Report)}
if($LoadNative){if(!$Report){throw '-LoadNative requires an explicit -Report path.'};$auditArguments+='--load-native'}
& $dotnet $binary @auditArguments
if($LASTEXITCODE -ne 0){throw 'Native package contains a forbidden dependency or legacy runtime endpoint; see the audit report.'}
