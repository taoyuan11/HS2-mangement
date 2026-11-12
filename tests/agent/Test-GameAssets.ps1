param(
    [Parameter(Mandatory)][string]$Game,
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$CSharpCompiler = '',
    [Parameter(Mandatory)][string]$Runner,
    [ValidateRange(15, 600)][int]$TimeoutSeconds = 120,
    [ValidateRange(1, 3)][int]$StartupAttempts = 2,
    [switch]$NoGraphics
)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $Game).Path
$testRoot = (Resolve-Path -LiteralPath $Sandbox).Path
$marker = Join-Path $testRoot 'agent-sandbox.json'
if ($testRoot -eq $sourceRoot -or $testRoot.StartsWith($sourceRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    !(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).purpose -ne 'hs2-agent-game-validation') {
    throw '必须使用 Prepare-GameSandbox.py 创建的独立游戏验收目录。'
}
if (!$CSharpCompiler) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation = & $vswhere -latest -products '*' -property installationPath
    $CSharpCompiler = Join-Path $installation 'MSBuild/Current/Bin/Roslyn/csc.exe'
}
$compiler = (Resolve-Path -LiteralPath $CSharpCompiler).Path
$references = @(@('HoneySelect2_Data/Managed/mscorlib.dll', 'HoneySelect2_Data/Managed/System.dll', 'HoneySelect2_Data/Managed/System.Core.dll', 'HoneySelect2_Data/Managed/Assembly-CSharp.dll', 'HoneySelect2_Data/Managed/IL.dll', 'BepInEx/core/BepInEx.dll') |
        ForEach-Object { '-r:' + (Join-Path $sourceRoot $_) }) + @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'HoneySelect2_Data/Managed') -Filter 'UnityEngine*.dll' |
        ForEach-Object { '-r:' + $_.FullName })
$plugin = Join-Path $testRoot 'BepInEx/plugins/GameAssetChecks.dll'
& $compiler -nologo -noconfig -nostdlib+ -target:library "-out:$plugin" @references (Join-Path $PSScriptRoot 'GameAssetChecks.cs')
if ($LASTEXITCODE -ne 0) {
    throw '游戏验收插件编译失败。'
}
$report = Join-Path $testRoot 'agent-test-result.json'
if (Test-Path -LiteralPath $report) {
    Remove-Item -LiteralPath $report
}
# Runner 以 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 管理游戏和子进程，超时／异常均回收。
for ($attempt = 1; $attempt -le $StartupAttempts; ++$attempt) {
    $runId = [guid]::NewGuid().ToString('N')
    $runArguments = @('-batchmode', '-force-d3d11', '-screen-width', '640', '-screen-height', '480', '-logFile', (Join-Path $testRoot "game-$runId.log"))
    if ($NoGraphics) {
        $runArguments += '-nographics'
    }
    & $Runner (Join-Path $testRoot 'HoneySelect2.exe') $testRoot (Join-Path $testRoot "process-$runId.log") $TimeoutSeconds @runArguments
    if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $report)) {
        break
    }
    if ($attempt -eq $StartupAttempts) {
        throw '游戏验收进程失败或超时。'
    }
    Write-Warning '游戏未在时限内生成验收报告，已回收进程，重新启动独立验收副本。'
}
if (!(Test-Path -LiteralPath $report)) {
    throw '游戏没有生成本轮验收报告。'
}
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (!$result.passed) {
    throw "游戏验收未通过：$report"
}
$result.items | Select-Object kind, card_loaded, character_loaded, generated_part_loaded, vertices, deformation_checked, clothing_states
