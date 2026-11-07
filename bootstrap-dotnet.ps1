[CmdletBinding()]
param([string]$Version = '10.0.403')
$ErrorActionPreference = 'Stop'
$toolsDirectory = Join-Path $PSScriptRoot '.tools'
$sdkDirectory = Join-Path $toolsDirectory 'dotnet'
if (Test-Path -LiteralPath (Join-Path $sdkDirectory 'dotnet.exe')) { Write-Output '本地 SDK 已存在。'; exit 0 }
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$sdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $Version)[0]
if (!$sdk) { throw "官方发布索引中没有 SDK $Version" }
$asset = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (!$asset -or ([Uri]$asset.url).Host -notin @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com')) { throw 'SDK 下载地址无效。' }
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$archive = Join-Path $toolsDirectory "dotnet-sdk-$Version-win-x64.zip"
Invoke-WebRequest $asset.url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $asset.hash) { throw 'SDK SHA-512 校验失败，未解压。' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory
Write-Output "SDK $Version 已准备：$sdkDirectory"
