# Called by start.cmd; all arguments belong to the application (including --data).
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'LocalWorkspace.ps1')
$project = Join-Path $workspace 'src\HS2Manager.App\HS2Manager.App.csproj'
$dotnetArguments = @('run', '--project', $project, '--configuration', 'Release', '--no-launch-profile', '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:BuildInParallel=false')
$env:HS2MANAGER_SOURCE_ROOT = $workspace
$env:HS2MANAGER_SOURCE_OUTPUT = $null
if ([HS2Manager.Core.StoragePaths]::IsNetwork($workspace)) {
    # Keep generated executables off SMB as well as the SQLite library. Some NAS
    # servers retain executable locks after exit, preventing the next rebuild.
    $configuration = Join-Path $workspace 'src\HS2Manager.App\bin\Release'
    $localRoot = Get-Hs2LocalCache $configuration
    $dotnetArguments += @('--artifacts-path', (Join-Path $localRoot 'Build'))
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $framework = [string]($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
    $runtime = [string]($projectXml.Project.PropertyGroup.RuntimeIdentifier | Where-Object { $_ } | Select-Object -First 1)
    $env:HS2MANAGER_SOURCE_OUTPUT = Join-Path (Join-Path $configuration $framework) $runtime
    Write-Output ('Local source build: ' + (Join-Path $localRoot 'Build'))
}
& $env:HS2_DOTNET @dotnetArguments -- @args
exit $LASTEXITCODE
