param([switch]$Unc)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Unc) {
    . (Join-Path $workspace 'scripts\LocalWorkspace.ps1')
    $workspace = [HS2Manager.Core.StoragePaths]::Identity($workspace)
    if (!$workspace.StartsWith('\\')) { throw 'UNC launch check requires a network checkout.' }
}
$output = Join-Path $workspace ('artifacts\source-launch-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('HS2Manager\Tests\source launch-' + [Guid]::NewGuid().ToString('N') + '\Data')
New-Item -ItemType Directory -Path $data -Force | Out-Null
$game = Join-Path $output 'NoGame'
New-Item -ItemType Directory -Path (Join-Path $game 'UserData\chara\female'), (Join-Path $game 'mods') -Force | Out-Null
@{ GamePath = $game } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
$entry = Join-Path $output 'launch.ps1'
$launch = "& '" + (Join-Path $workspace 'start.cmd').Replace("'", "''") + "' --data '" + $data.Replace("'", "''") + "' *> '" + (Join-Path $output 'launch.log').Replace("'", "''") + "'"
Set-Content -LiteralPath $entry -Value ($launch + "`nexit `$LASTEXITCODE") -Encoding utf8
$wrapper = @'
using System;
using System.Threading.Tasks;
public static class PreviewSourceLaunch
{
    private static readonly System.Threading.CancellationTokenSource cancellation = new System.Threading.CancellationTokenSource();
    public static void Stop() { cancellation.Cancel(); }
    public static Task<int> Run(string shell, string workspace, string script)
    {
        return Task.Run(() => HS2Manager.BuildTools.OwnedToolProcess.Run(shell,
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }, workspace, 240, null, null, cancellation.Token));
    }
}
'@
Add-Type -TypeDefinition ((Get-Content -LiteralPath (Join-Path $workspace 'scripts\OwnedToolProcess.cs') -Raw) + "`n" + $wrapper.Replace('using System;', '').Replace('using System.Threading.Tasks;', '').Replace('Task<int>', 'System.Threading.Tasks.Task<int>').Replace('Task.Run', 'System.Threading.Tasks.Task.Run'))
$running = [PreviewSourceLaunch]::Run((Get-Command powershell.exe).Source, $workspace, $entry)
$ownedApp = $null
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    do {
        if ($running.IsCompleted) { throw "Source launcher exited early: $($running.GetAwaiter().GetResult())" }
        $candidate = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'HS2Manager.exe' -and $_.CommandLine -and $_.CommandLine.Contains($data) } | Select-Object -First 1
        if ($candidate) {
            $ownedApp = Get-Process -Id $candidate.ProcessId
            if ($ownedApp.MainWindowHandle -ne 0 -and $ownedApp.MainWindowTitle -eq 'HS2 Manager') { break }
            $ownedApp.Dispose(); $ownedApp = $null
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$ownedApp -or $ownedApp.MainWindowHandle -eq 0 -or $ownedApp.MainWindowTitle -ne 'HS2 Manager') { throw 'Source launch did not show the application window.' }
    if (!(Test-Path -LiteralPath (Join-Path $data 'library.db'))) { throw 'Source launch did not create the isolated local library.' }
    if (!$ownedApp.CloseMainWindow()) { throw 'Source application could not close normally.' }
    if (!$running.Wait(20000)) { throw 'Source launcher did not exit after application close.' }
    if ($running.GetAwaiter().GetResult() -ne 0) { throw 'Source launcher exited unsuccessfully.' }
    if (!$ownedApp.WaitForExit(5000)) { throw 'Owned source application survived launch check.' }
    Write-Output "PASS start.cmd Release build, application startup and normal shutdown; PID $($ownedApp.Id) stopped"
}
finally {
    if ($ownedApp) { if (!$ownedApp.HasExited) { $ownedApp.Kill($true); $ownedApp.WaitForExit() }; $ownedApp.Dispose() }
    if (!$running.IsCompleted) { [PreviewSourceLaunch]::Stop(); try { $running.Wait(5000) | Out-Null } catch {} }
}
