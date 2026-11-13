function Get-AppVersion {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        @($version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count) {
        throw 'VERSION 必须是三段数字版本，每段不超过 65535。'
    }
    return $version
}
