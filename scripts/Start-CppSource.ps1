# Called by start.cmd. Arguments after the script belong to HS2Manager.exe.
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$result=Join-Path $env:LOCALAPPDATA ('HS2Manager/CppBuild/launch-'+[Guid]::NewGuid().ToString('N')+'.json')
$previousSource=$env:HS2MANAGER_SOURCE_ROOT
$previousOutput=$env:HS2MANAGER_SOURCE_OUTPUT
$launchArguments=@($args)
$code=1
try {
    & (Join-Path $source 'build-cpp.ps1') -ResultFile $result
    $build=Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if(!(Test-Path -LiteralPath $build.Executable -PathType Leaf)){throw '原生构建没有生成程序。'}
    $env:HS2MANAGER_SOURCE_ROOT=$source
    # Keep the existing source-library identity, including mapped-drive aliases.
    # DataLocation recognises this historical identity after the C# sources are removed.
    $env:HS2MANAGER_SOURCE_OUTPUT=Join-Path $source 'src/HS2Manager.App/bin/Release/net10.0-windows10.0.19041.0/win-x64'
    & $build.Executable @launchArguments | Out-Host
    $code=$LASTEXITCODE
} finally {
    $env:HS2MANAGER_SOURCE_ROOT=$previousSource
    $env:HS2MANAGER_SOURCE_OUTPUT=$previousOutput
    if(Test-Path -LiteralPath $result){Remove-Item -LiteralPath $result -Force}
}
exit $code
