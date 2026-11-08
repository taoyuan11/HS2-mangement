[CmdletBinding()]
param([string]$SevenZip = 'C:\Program Files\7-Zip\7z.exe')
$ErrorActionPreference = 'Stop'
$version = '2018.4.11f1'
$directory = Join-Path $PSScriptRoot '.tools\unity'
$destination = Join-Path $directory $version
$editor = Join-Path $destination 'Editor\Unity.exe'
$player = Join-Path $destination 'Editor\Data\PlaybackEngines\windowsstandalonesupport'
if ((Test-Path -LiteralPath $editor) -and (Test-Path -LiteralPath $player)) {
    if ((Get-Item -LiteralPath $editor).VersionInfo.ProductVersion -eq '2018.4.11.7379119') {
        Write-Output "Unity editor present: $editor"
        Write-Output 'A valid Unity license is required for building; this script does not activate a license.'
        return
    }
}
if (!(Test-Path -LiteralPath $SevenZip)) { throw '7-Zip is required to extract the official editor into .tools. Specify -SevenZip <7z.exe>.' }
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$installer = Join-Path $directory "UnitySetup64-$version.exe"
$expectedMd5 = '2002754972965D9B019EEDEB3DF832FA'
# Published by the official unity-2018.4.11f1-win.ini; also verify the publisher signature below.
if (!(Test-Path -LiteralPath $installer) -or (Get-FileHash -LiteralPath $installer -Algorithm MD5).Hash -ne $expectedMd5) {
    $partial = $installer + '.partial'
    & curl.exe --fail --location --silent --show-error --max-time 900 --speed-limit 10000 --speed-time 30 --output $partial 'https://download.unity3d.com/download_unity/7098af2f11ea/Windows64EditorInstaller/UnitySetup64-2018.4.11f1.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Official Unity download failed; no editor was installed.' }
    if ((Get-FileHash -LiteralPath $partial -Algorithm MD5).Hash -ne $expectedMd5) { throw 'Official Unity installer checksum mismatch.' }
    Move-Item -LiteralPath $partial -Destination $installer -Force
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Unity Technologies') { throw 'Unity installer publisher signature validation failed.' }
if (!('HS2Manager.BuildTools.OwnedToolProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'scripts\OwnedToolProcess.cs') }
$code = [HS2Manager.BuildTools.OwnedToolProcess]::Run($SevenZip, @('x', $installer, ('-o' + $destination), 'Editor\*', '-y', '-bso0', '-bsp0'), $PSScriptRoot, 600, $null)
if ($code -gt 1 -or !(Test-Path -LiteralPath $editor)) { throw "Unity extraction failed ($code)." }
Write-Output "Unity editor installed: $editor"
Write-Output 'Activate this machine with your Unity account before running build-unity-preview.ps1.'
