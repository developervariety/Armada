$ErrorActionPreference = 'Stop'

$armadaHome = Join-Path $env:USERPROFILE '.armada'
$serverExe = Join-Path $armadaHome 'bin\Armada.Server.exe'
$targetPath = [System.IO.Path]::GetFullPath($serverExe)

if (-not (Test-Path -LiteralPath $targetPath)) {
    Write-Error "Published server executable not found at $targetPath"
}

$existing = Get-CimInstance Win32_Process -Filter "Name = 'Armada.Server.exe'" |
    Where-Object {
        $_.ExecutablePath -and
        [System.StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $targetPath)
    }

if ($existing) {
    exit 0
}

Start-Process -FilePath $targetPath -WorkingDirectory $armadaHome -WindowStyle Hidden | Out-Null
exit 0
