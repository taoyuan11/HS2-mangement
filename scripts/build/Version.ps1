function Get-AppVersion {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
    Assert-AppVersion $version
    return $version
}

function Assert-AppVersion([string]$Version) {
    # 限制数字长度后再转换，避免超长输入溢出；协议只接受 ASCII 数字。
    if ($Version -cnotmatch '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z' -or
        @($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) {
        throw 'VERSION 必须是三段数字版本，每段不超过 65535。'
    }
}
