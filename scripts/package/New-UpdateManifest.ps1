[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Archive, [Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Version.ps1')
if ($Version -ne (Get-AppVersion)) {
    throw '更新清单版本与 VERSION 不符。'
}
$file = Get-Item -LiteralPath $Archive
$name = "HS2Manager-$Version-cpp-win-x64.zip"
if ($file.Name -ne $name) {
    throw "发布 ZIP 必须命名为 $name。"
}
$hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $file.DirectoryName ($name + '.sha256')), "$hash  $name`n", $utf8)
$manifest = [ordered]@{ schemaVersion = 1
    version = $Version
    platform = 'win-x64'
    archive = $name
    bytes = $file.Length
    sha256 = $hash
}
[IO.File]::WriteAllText((Join-Path $file.DirectoryName 'update.json'), ($manifest | ConvertTo-Json) + "`n", $utf8)
