[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Python = 'python',
    [string]$ClangFormat = 'clang-format',
    [string]$AnalyzerModule = 'PSScriptAnalyzer'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module $AnalyzerModule -ErrorAction Stop
$settings = Join-Path $repository 'PSScriptAnalyzerSettings.psd1'

function Format-PowerShellSource {
    param([string]$Source)

    $Source = $Source.Replace("`r`n", "`n")
    $tokens = $null
    $parseErrors = $null
    $syntax = [Management.Automation.Language.Parser]::ParseInput($Source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) {
        throw ($parseErrors.Message -join [Environment]::NewLine)
    }

    $loops = $syntax.FindAll({ param($node) $node -is [Management.Automation.Language.ForStatementAst] }, $true)
    $replacements = @()
    foreach ($token in $tokens) {
        if ($token.Kind -ne [Management.Automation.Language.TokenKind]::Semi) {
            continue
        }

        # for 的三个表达式由分号分隔，其余语句和哈希表条目分别换行。
        $offset = $token.Extent.StartOffset
        $inLoopHeader = @($loops | Where-Object {
                $offset -ge $_.Extent.StartOffset -and $offset -lt $_.Body.Extent.StartOffset
            }).Count -gt 0
        if (!$inLoopHeader) {
            $replacements += $token
        }
    }

    foreach ($token in ($replacements | Sort-Object { $_.Extent.StartOffset } -Descending)) {
        $Source = $Source.Remove($token.Extent.StartOffset, $token.Extent.EndOffset - $token.Extent.StartOffset)
        $Source = $Source.Insert($token.Extent.StartOffset, "`n")
    }

    for ($pass = 0; $pass -lt 8; $pass++) {
        $formatted = (Invoke-Formatter -ScriptDefinition $Source -Settings $settings).Replace("`r`n", "`n")
        if ($formatted -ceq $Source) {
            break
        }
        $Source = $formatted
    }
    $syntax = [Management.Automation.Language.Parser]::ParseInput($Source, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) {
        throw ($parseErrors.Message -join [Environment]::NewLine)
    }

    $lines = $Source -split '\r?\n'
    $boundaries = @{}
    $literalLines = @{}
    foreach ($token in $tokens) {
        if ($token.Kind -in @('HereStringLiteral', 'HereStringExpandable', 'StringLiteral', 'StringExpandable', 'Comment') -and
            $token.Extent.EndLineNumber -gt $token.Extent.StartLineNumber) {
            for ($line = $token.Extent.StartLineNumber - 1; $line -lt $token.Extent.EndLineNumber; $line++) {
                $literalLines[$line] = $true
            }
        }
    }
    $functions = $syntax.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)
    foreach ($function in $functions) {
        $boundaries[$function.Extent.StartLineNumber - 1] = $true
        $boundaries[$function.Extent.EndLineNumber] = $true
    }

    # 函数之间保留空行，同时避免在文件首尾积累空白。
    $result = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($literalLines.ContainsKey($index)) {
            $result.Add($lines[$index])
            continue
        }
        if (!$lines[$index].Trim() -and $result.Count -and !$result[$result.Count - 1].Trim()) {
            continue
        }
        if ($boundaries.ContainsKey($index) -and $lines[$index].Trim() -and $result.Count -and
            $result[$result.Count - 1].Trim() -and !$result[$result.Count - 1].TrimStart().StartsWith('#')) {
            $result.Add('')
        }
        $result.Add($lines[$index].TrimEnd())
    }
    return (($result -join "`n").TrimEnd() + "`n")
}

Push-Location $repository
try {
    $arguments = @((Join-Path $PSScriptRoot 'format_code.py'), '--clang-format', $ClangFormat)
    if ($Check) {
        $arguments += '--check'
    }
    & $Python @arguments
    if ($LASTEXITCODE -ne 0) {
        throw '源码格式检查失败。'
    }

    $changed = @()
    $count = 0
    $files = & git ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) {
        throw '无法读取 Git 文件列表。'
    }
    foreach ($path in ($files | Sort-Object -Unique)) {
        if ($path -notmatch '\.ps[dm]?1$' -or !(Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        $fullPath = Join-Path $repository $path
        $source = [IO.File]::ReadAllText($fullPath)
        $bytes = [IO.File]::ReadAllBytes($fullPath)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $formatted = Format-PowerShellSource $source
        $count++
        if ($formatted -cne $source.Replace("`r`n", "`n") -or !$hasBom) {
            $changed += $path
            if (!$Check) {
                [IO.File]::WriteAllText($fullPath, $formatted, [Text.UTF8Encoding]::new($true))
            }
        }
    }
    if ($Check -and $changed.Count) {
        throw ("以下 PowerShell 文件需要格式化：`n" + ($changed -join "`n"))
    }
    Write-Output "已处理 $count 个 PowerShell 文件，需调整 $($changed.Count) 个。"
}
finally {
    Pop-Location
}
