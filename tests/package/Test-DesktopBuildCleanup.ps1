[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (!('HS2Manager.Build.DesktopBuildCleanup' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/package/DesktopBuildCleanup.cs')
}
if (!('DesktopCleanupTestLibrary' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public sealed class DesktopCleanupTestLibrary : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
    private IntPtr module;
    public DesktopCleanupTestLibrary(string path)
    {
        module = LoadLibraryW(path);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public bool HasExport()
    {
        return GetProcAddress(module, "GetFileVersionInfoSizeW") != IntPtr.Zero;
    }
    public void Dispose()
    {
        if (module != IntPtr.Zero)
            FreeLibrary(module);
        module = IntPtr.Zero;
    }
}
'@
}
$temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $temporary ('hs2-desktop-cleanup-check-' + [Guid]::NewGuid().ToString('N'))
$owned = [Collections.Generic.List[string]]::new()
$library = $null
$stream = $null
$mapping = $null
$view = $null
$junction = $null
. (Join-Path $PSScriptRoot '../../scripts/package/DesktopBuildCleanup.ps1')
$registeredRunner = $null
$stateDirectory = $null

# Exercise the real persisted queue without changing this user's logon registration.
function Get-DesktopCleanupStateDirectory {
    return $stateDirectory 
}
function Get-DesktopCleanupDesktop {
    return $root 
}
function Set-DesktopCleanupRunOnce([string]$Runner) {
    $script:registeredRunner = $Runner 
}
function Clear-DesktopCleanupRunOnce {
    $script:registeredRunner = $null 
}

function New-CheckDirectory {
    $path = Join-Path $root ('HS2Manager-backup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    $owned.Add($path)
    return $path
}

function Remove-CheckDirectory([string]$Path) {
    [HS2Manager.Build.DesktopBuildCleanup]::Remove($Path, $root, [IO.Path]::GetFileName($Path))
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (!$Condition) {
        throw $Message
    }
}

try {
    $stateDirectory = New-CheckDirectory
    $path = New-CheckDirectory
    $nested = Join-Path $path 'nested'
    New-Item -ItemType Directory -Path $nested | Out-Null
    $readOnly = Join-Path $nested 'readonly.txt'
    [IO.File]::WriteAllText($readOnly, 'read only')
    [IO.File]::SetAttributes($readOnly, [IO.FileAttributes]::ReadOnly)
    Remove-CheckDirectory $path
    Assert-Check (!(Test-Path -LiteralPath $path)) '普通和只读文件未清理。'
    Remove-CheckDirectory $path

    $path = New-CheckDirectory
    $dll = Join-Path $path 'version.dll'
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32/version.dll') -Destination $dll
    $library = [DesktopCleanupTestLibrary]::new($dll)
    $denied = $false
    try {
        [IO.File]::Delete($dll)
    }
    catch {
        $denied = $true
    }
    Assert-Check $denied '未复现已加载 DLL 的普通删除失败。'
    Register-DesktopBuildCleanup $path
    $record = Join-Path $stateDirectory ([IO.Path]::GetFileName($path) + '.json')
    Assert-Check (Test-Path -LiteralPath $registeredRunner) '未创建独立的登录清理脚本。'
    Invoke-PendingDesktopBuildCleanup 3>$null
    Assert-Check (Test-Path -LiteralPath $record) '占用未释放时丢失了待清理记录。'
    Assert-Check ($library.HasExport()) '清理重试影响了已有 DLL 映射。'
    $library.Dispose()
    $library = $null
    Invoke-PendingDesktopBuildCleanup
    Assert-Check (!(Test-Path -LiteralPath $path)) 'DLL 释放后重试未清理旧目录。'
    Assert-Check (!(Test-Path -LiteralPath $record)) '成功清理后保留了过时记录。'
    Assert-Check (!$registeredRunner) '队列清空后未注销登录清理。'

    $path = New-CheckDirectory
    $mapped = Join-Path $path 'mapped.bin'
    [IO.File]::WriteAllBytes($mapped, [byte[]]@(42, 7, 19))
    $stream = [IO.File]::Open($mapped, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $mapping = [IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile($stream, ('HS2CleanupTest-' + [Guid]::NewGuid().ToString('N')), 0,
        [IO.MemoryMappedFiles.MemoryMappedFileAccess]::ReadWrite, [IO.HandleInheritability]::None, $true)
    $view = $mapping.CreateViewAccessor()
    Register-DesktopBuildCleanup $path
    Invoke-PendingDesktopBuildCleanup 3>$null
    Assert-Check ($view.ReadByte(0) -eq 42) '清理重试影响了已有文件映射。'
    $view.Dispose()
    $view = $null
    $mapping.Dispose()
    $mapping = $null
    $stream.Dispose()
    $stream = $null
    Invoke-PendingDesktopBuildCleanup
    Assert-Check (!(Test-Path -LiteralPath $path)) '映射释放后重试未清理旧目录。'

    $path = New-CheckDirectory
    $locked = Join-Path $path 'locked.dll'
    $other = Join-Path $path 'other.txt'
    [IO.File]::WriteAllText($locked, 'still in use')
    [IO.File]::WriteAllText($other, 'can be removed')
    $stream = [IO.File]::Open($locked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $denied = $false
    try {
        Remove-CheckDirectory $path
    }
    catch {
        $denied = $_.Exception.ToString().Contains('locked.dll')
    }
    Assert-Check $denied '未报告拒绝共享删除的文件路径。'
    Assert-Check (Test-Path -LiteralPath $locked) '错误删除了拒绝共享删除的文件。'
    Assert-Check (!(Test-Path -LiteralPath $other)) '单个占用文件阻止了其它文件清理。'
    $stream.Dispose()
    $stream = $null
    Remove-CheckDirectory $path
    Assert-Check (!(Test-Path -LiteralPath $path)) '占用释放后重试清理失败。'

    $path = New-CheckDirectory
    $sentinel = Join-Path $path 'preserve.txt'
    [IO.File]::WriteAllText($sentinel, 'preserve')
    foreach ($invalid in @(
            @{Path = $path; Parent = $temporary; Name = [IO.Path]::GetFileName($path) },
            @{Path = $root; Parent = $temporary; Name = [IO.Path]::GetFileName($root) },
            @{Path = $path; Parent = $root; Name = 'HS2Manager' },
            @{Path = $path; Parent = $root; Name = ('HS2Manager-backup-' + ('0' * 32)) }
        )) {
        $denied = $false
        try {
            [HS2Manager.Build.DesktopBuildCleanup]::Remove($invalid.Path, $invalid.Parent, $invalid.Name)
        }
        catch {
            $denied = $true
        }
        Assert-Check $denied '清理接受了越界或非本次生成的目录。'
        Assert-Check (Test-Path -LiteralPath $sentinel) '边界检查删除了受保护文件。'
    }
    $linked = New-CheckDirectory
    $junction = Join-Path $linked 'outside'
    New-Item -ItemType Junction -Path $junction -Target $path | Out-Null
    $denied = $false
    try {
        Remove-CheckDirectory $linked
    }
    catch {
        $denied = $true
    }
    Assert-Check $denied '清理接受了目录联接。'
    Assert-Check (Test-Path -LiteralPath $sentinel) '清理越过了目录联接。'
    [IO.Directory]::Delete($junction, $false)
    $junction = $null
    Remove-CheckDirectory $linked
    Register-DesktopBuildCleanup $path
    $record = Join-Path $stateDirectory ([IO.Path]::GetFileName($path) + '.json')
    @{Desktop = $temporary; Name = [IO.Path]::GetFileName($path) } | ConvertTo-Json | Set-Content -LiteralPath $record -Encoding UTF8
    Invoke-PendingDesktopBuildCleanup 3>$null
    Assert-Check (Test-Path -LiteralPath $sentinel) '清理接受了篡改的桌面路径。'
    Assert-Check (Test-Path -LiteralPath $record) '无效清理记录被静默移除。'
    Register-DesktopBuildCleanup $path
    Invoke-PendingDesktopBuildCleanup
    Remove-CheckDirectory $path
    Write-Output 'PASS: loaded DLL and mapped file deferred cleanup, persisted queue, read-only files, sharing lock, retry, sibling cleanup, path boundaries, invalid records and junction rejection.'
}
finally {
    if ($library) {
        $library.Dispose() 
    }
    if ($view) {
        $view.Dispose() 
    }
    if ($mapping) {
        $mapping.Dispose() 
    }
    if ($stream) {
        $stream.Dispose() 
    }
    $resolved = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporary -or
        [IO.Path]::GetFileName($resolved) -notmatch '^hs2-desktop-cleanup-check-[0-9a-f]{32}$') {
        throw '测试清理目录越界。'
    }
    if ($junction -and [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($junction)) -eq $resolved) {
        [IO.Directory]::Delete($junction, $false)
    }
    foreach ($path in $owned) {
        Remove-CheckDirectory $path
    }
    if (Test-Path -LiteralPath $resolved) {
        [IO.Directory]::Delete($resolved, $false)
    }
}
