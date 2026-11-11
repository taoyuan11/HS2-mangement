# Unity 2018 cannot resolve the SDK's optional 2022 test assemblies. Embed the
# verified official package without its tests; runtime/editor binaries are unchanged.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$embedded = Join-Path $repo 'tests/reference/src/HS2Manager.UnityPreview/Packages/com.autodesk.fbx'
if (Test-Path -LiteralPath (Join-Path $embedded 'package.json')) { return }
$archive = Join-Path $repo '.tools/fbx-4.2.0.tgz'
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri 'https://download.packages.unity.com/com.autodesk.fbx/-/com.autodesk.fbx-4.2.0.tgz' -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash -ne '3E3E73B060DE518027EB9692236AA524A3D34F24') { throw 'FBX SDK package checksum mismatch.' }
$unpacked = Join-Path $repo ('.tools/fbx-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $unpacked -Force | Out-Null
try {
    & tar -xzf $archive -C $unpacked
    if ($LASTEXITCODE -ne 0) { throw 'FBX SDK extraction failed.' }
    New-Item -ItemType Directory -Path $embedded -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $unpacked 'package') -Force | Where-Object { $_.Name -notin @('Tests', 'Tests.meta') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $embedded -Recurse -Force }
}
finally {
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo '.tools')) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($unpacked).StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
}
