[CmdletBinding()]
param([Parameter(Position = 0)][string]$Version = '')

$ErrorActionPreference = 'Stop'
# 原生命令的退出码由统一入口处理，兼容 Windows PowerShell 5.1 与 PowerShell 7。
$PSNativeCommandUseErrorActionPreference = $false
$previousConsoleEncoding = [Console]::OutputEncoding
$previousOutputEncoding = $OutputEncoding
# Git 输出 UTF-8；按系统代码页解码会破坏中文仓库路径和远端地址。
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repository 'scripts/build/Version.ps1')

function Invoke-ReleaseGit([string[]]$Arguments, [int[]]$AllowedExitCodes = @(0)) {
    $output = @(& git -C $repository @Arguments)
    $code = $LASTEXITCODE
    if ($code -notin $AllowedExitCodes) {
        throw "Git 命令失败（退出码 $code）：git $($Arguments[0])。请查看上方 Git 错误。"
    }
    return [pscustomobject]@{
        ExitCode = $code
        Text = ($output -join "`n").Trim()
    }
}

try {
    if (!$Version) {
        throw '用法：push-releases.cmd <版本号>，例如 push-releases.cmd 1.5.1。'
    }
    if ($Version.StartsWith('v', [StringComparison]::Ordinal)) {
        $Version = $Version.Substring(1)
    }
    Assert-AppVersion $Version
    $tag = "v$Version"
    $null = Get-Command git -ErrorAction Stop

    $branch = Invoke-ReleaseGit @('symbolic-ref', '--quiet', '--short', 'HEAD') @(0, 1)
    if ($branch.ExitCode -ne 0 -or $branch.Text -cne 'main') {
        throw '只能从 main 分支发布，请先切换到 main 并整理待发布代码。'
    }
    # 使用实际推送地址检查远端；拒绝多目标推送，以保证原子性只涉及一个远端。
    $remote = Invoke-ReleaseGit @('remote', 'get-url', '--push', '--all', 'origin')
    $pushUrls = @($remote.Text -split "`n" | Where-Object { $_ })
    if ($pushUrls.Count -ne 1) {
        throw 'origin 必须配置且只有一个推送地址。'
    }
    $pushUrl = $pushUrls[0]
    foreach ($state in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply', 'sequencer')) {
        $statePath = (Invoke-ReleaseGit @('rev-parse', '--git-path', $state)).Text
        if (![IO.Path]::IsPathRooted($statePath)) {
            $statePath = Join-Path $repository $statePath
        }
        if (Test-Path -LiteralPath $statePath) {
            throw '存在未完成的合并、变基或拣选操作，请先完成或取消该操作。'
        }
    }
    if ((Invoke-ReleaseGit @('ls-files', '--unmerged')).Text) {
        throw '存在未解决的冲突，请先解决冲突。'
    }
    $null = Invoke-ReleaseGit @('var', 'GIT_AUTHOR_IDENT')
    $null = Invoke-ReleaseGit @('var', 'GIT_COMMITTER_IDENT')
    $localTag = Invoke-ReleaseGit @('show-ref', '--verify', '--quiet', "refs/tags/$tag") @(0, 1)
    if ($localTag.ExitCode -eq 0) {
        throw "本地标签 $tag 已存在，不会覆盖。若此前推送失败，请使用当时输出的恢复命令。"
    }
    Write-Output "正在检查 origin/main 和 $tag ..."
    $refs = Invoke-ReleaseGit @('ls-remote', '--refs', $pushUrl, 'refs/heads/main', "refs/tags/$tag")
    $remoteMain = ''
    foreach ($line in ($refs.Text -split "`n")) {
        $parts = $line -split "`t", 2
        if ($parts.Count -ne 2) {
            continue
        }
        if ($parts[1] -ceq "refs/tags/$tag") {
            throw "远端标签 $tag 已存在，请使用新的版本号。"
        }
        if ($parts[1] -ceq 'refs/heads/main') {
            $remoteMain = $parts[0]
        }
    }
    if (!$remoteMain) {
        throw 'origin 上不存在 main 分支，无法校验发布基线。'
    }
    # 只取回对象，不自动合并、不改写本地分支、索引或工作区。
    $null = Invoke-ReleaseGit @('fetch', '--no-tags', '--no-write-fetch-head', $pushUrl, 'refs/heads/main')
    $ancestor = Invoke-ReleaseGit @('merge-base', '--is-ancestor', $remoteMain, 'HEAD') @(0, 1)
    if ($ancestor.ExitCode -ne 0) {
        throw '本地 main 尚未包含远端最新提交，请先同步并解决分叉后重试。'
    }

    $versionPath = Join-Path $repository 'VERSION'
    $null = Get-AppVersion
    [IO.File]::WriteAllText($versionPath, "$Version`n", [Text.UTF8Encoding]::new($false))
    Write-Output "正在提交全部未忽略改动，发布版本 $tag ..."
    $null = Invoke-ReleaseGit @('add', '-A', '--', '.')
    $diff = Invoke-ReleaseGit @('diff', '--cached', '--quiet', '--exit-code') @(0, 1)
    if ($diff.ExitCode -eq 1) {
        $commit = Invoke-ReleaseGit @('commit', '-m', "chore(release): $tag")
        Write-Output $commit.Text
    }
    # 钩子可能改变文件或提交内容；标签只能指向包含本次版本的完整提交。
    if ((Invoke-ReleaseGit @('status', '--porcelain', '--untracked-files=all')).Text -or
        (Invoke-ReleaseGit @('show', 'HEAD:VERSION')).Text -cne $Version -or
        (Invoke-ReleaseGit @('symbolic-ref', '--quiet', '--short', 'HEAD')).Text -cne 'main') {
        throw '提交后的工作区、分支或 VERSION 与发布目标不一致，请检查 Git 钩子及文件改动。'
    }
    $null = Invoke-ReleaseGit @('tag', '-a', $tag, '-m', "Release $tag")
    $retry = "git -c remote.origin.mirror=false push --atomic --no-follow-tags origin refs/heads/main:refs/heads/main refs/tags/${tag}:refs/tags/${tag}"
    try {
        $pushed = Invoke-ReleaseGit @('-c', 'remote.origin.mirror=false', 'push', '--atomic', '--no-follow-tags',
            'origin', 'refs/heads/main:refs/heads/main', "refs/tags/${tag}:refs/tags/${tag}")
        if ($pushed.Text) {
            Write-Output $pushed.Text
        }
    }
    catch {
        Write-Host "推送未确认成功；本地提交和标签 $tag 已保留。请检查远端状态并解决 Git 报告的问题。"
        Write-Host "在本仓库中恢复推送：`n$retry"
        throw
    }
    Write-Output "已推送 main 和 $tag。GitHub Actions 将继续构建，构建成功后才会公开发布。"
    # 同时识别 HTTPS 与 SSH 地址；本地测试远端不生成虚假的 GitHub 链接。
    if ($pushUrl -match '\A(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)([^/]+/[^/]+?)(?:\.git)?/?\z') {
        $web = 'https://github.com/' + $Matches[1]
        Write-Output "标签：$web/tree/$tag"
        Write-Output "Actions：$web/actions"
        Write-Output "Releases：$web/releases/tag/$tag"
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
finally {
    [Console]::OutputEncoding = $previousConsoleEncoding
    $OutputEncoding = $previousOutputEncoding
}
exit 0
