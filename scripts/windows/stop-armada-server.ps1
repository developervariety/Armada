$ErrorActionPreference = 'Stop'

$armadaHome = Join-Path $env:USERPROFILE '.armada'
$serverExe = Join-Path $armadaHome 'bin\Armada.Server.exe'
$targetPath = [System.IO.Path]::GetFullPath($serverExe)

$processes = @(Get-CimInstance Win32_Process -Filter "Name = 'Armada.Server.exe'" |
    Where-Object {
        $_.ExecutablePath -and
        [System.StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $targetPath)
    })

foreach ($process in $processes) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
}

for ($attempt = 0; $attempt -lt 10; $attempt++) {
    $stillRunning = @(Get-CimInstance Win32_Process -Filter "Name = 'Armada.Server.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            [System.StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $targetPath)
        })

    if ($stillRunning.Count -eq 0) {
        exit 0
    }

    Start-Sleep -Seconds 1
}

Write-Error "Armada.Server.exe did not stop cleanly at $targetPath"
