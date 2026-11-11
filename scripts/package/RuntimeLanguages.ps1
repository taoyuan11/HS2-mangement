# 项目仅提供中文界面；保留简体中文及 WinUI 的英文最终回退资源。
# 只过滤发布根目录下的语言专用 MUI 文件，不处理 PRI、XAML 或其他资源目录。
function Test-UnusedRuntimeLanguageFile([string]$RelativePath) {
    if ($RelativePath -notmatch '^([a-z]{2,3}(?:-[a-z0-9]{2,8})+)[\\/][^\\/]+\.mui$') {
        return $false
    }
    return $Matches[1] -notin @('zh-CN', 'en-US')
}
