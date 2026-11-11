[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (!('HS2Manager.Build.PackageFiles' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/package/PackageFiles.cs')
}
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $temporary ('hs2-package-check-' + [Guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $root 'source'
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $source 'empty'), [byte[]]@())
    [IO.File]::WriteAllText((Join-Path $source '中文.txt'), '并行发布资源')
    $random = [Random]::new(42)
    $bytes = [byte[]]::new(1024 * 1024 + 17)
    $random.NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $source 'binary.dll'), $bytes)
    $files = @(Get-ChildItem -LiteralPath $source -File | Sort-Object Name)
    $sources = [string[]]@($files.FullName)
    $expected = $null
    foreach ($jobs in @(1, 4)) {
        $output = Join-Path $root "result-$jobs"
        $targets = [string[]]@($files | ForEach-Object { Join-Path $output ('nested/' + $_.Name) })
        [HS2Manager.Build.PackageFiles]::Copy($sources, $targets, $jobs)
        $manifest = [HS2Manager.Build.PackageFiles]::Manifest($output, $targets, $jobs)
        for ($index = 0; $index -lt $files.Count; $index++) {
            if ($manifest[$index].SHA256 -ne (Get-FileHash -LiteralPath $sources[$index]).Hash -or
                $manifest[$index].Bytes -ne $files[$index].Length) {
                throw '并行复制改变了文件内容或清单顺序。'
            }
        }
        $json = $manifest | ConvertTo-Json
        if ($expected -and $expected -cne $json) {
            throw '串行和并行发布清单不一致。'
        }
        $expected = $json
    }
    $rejected = $false
    try {
        [HS2Manager.Build.PackageFiles]::Copy(@($sources[0], (Join-Path $source 'missing')),
            @((Join-Path $root 'failure/a'), (Join-Path $root 'failure/b')), 4)
    }
    catch {
        $rejected = $true
    }
    if (!$rejected) {
        throw '并行复制吞掉了缺失文件错误。'
    }
    $rejected = $false
    try {
        [void][HS2Manager.Build.PackageFiles]::Manifest((Join-Path $root 'result-4'), $sources, 4)
    }
    catch {
        $rejected = $true
    }
    if (!$rejected) {
        throw '清单接受了包目录之外的文件。'
    }
    Write-Output 'PASS: parallel package contents, deterministic manifest, worker failure and path boundary.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporary -or
        ![IO.Path]::GetFileName($resolved).StartsWith('hs2-package-check-')) {
        throw '测试清理目录越界。'
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
