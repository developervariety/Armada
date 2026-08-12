$ErrorActionPreference = 'Stop'

# Stop EVERY running Armada Admiral server, no matter where it was launched from:
#   - the managed publish at %USERPROFILE%\.armada\bin\Armada.Server.exe
#   - a stray build run straight out of the repo (src\Armada.Server\bin\Debug|Release)
#   - a 'dotnet run' / 'dotnet exec' host of Armada.Server.dll
# The update scripts rely on this to fully release the Admiral/MCP ports and the build
# output file locks before republishing; leaving a stray instance alive is exactly what
# makes an update appear to "not take" (the old server keeps serving the port).

function Get-ArmadaServerProcesses {
    $byExe = @(Get-CimInstance Win32_Process -Filter "Name = 'Armada.Server.exe'" -ErrorAction SilentlyContinue)
    $byDotnet = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match 'Armada\.Server\.dll' })
    return @($byExe + $byDotnet)
}

$targets = Get-ArmadaServerProcesses
if ($targets.Count -gt 0) {
    foreach ($process in $targets) {
        $where = if ($process.ExecutablePath) { $process.ExecutablePath } else { $process.Name }
        Write-Host "[stop-armada-server] Stopping PID $($process.ProcessId) ($where)"
        try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop } catch { }
    }
}

# Wait for every instance to exit so ports and DLLs are released before the caller rebuilds.
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $stillRunning = Get-ArmadaServerProcesses
    if ($stillRunning.Count -eq 0) {
        exit 0
    }

    Start-Sleep -Milliseconds 500
}

$remaining = (Get-ArmadaServerProcesses | ForEach-Object { $_.ProcessId }) -join ', '
Write-Error "Armada.Server did not stop cleanly; still running PID(s): $remaining"
exit 1
