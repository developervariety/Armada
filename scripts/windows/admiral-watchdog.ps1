param(
    [Parameter(Mandatory = $true)]
    [string]$WorkingDirectory,

    [Parameter(Mandatory = $true)]
    [int]$AdmiralPid,

    [Parameter(Mandatory = $true)]
    [string]$ServerDll,

    [int]$ShutdownWaitSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-Location $WorkingDirectory

$deadline = (Get-Date).AddSeconds($ShutdownWaitSeconds)
while ((Get-Process -Id $AdmiralPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
    Start-Sleep -Seconds 1
}

if (Get-Process -Id $AdmiralPid -ErrorAction SilentlyContinue) {
    Stop-Process -Id $AdmiralPid -Force
}

$serverDir = Split-Path -Parent $ServerDll
if (-not (Test-Path $ServerDll)) {
    Write-Error "Server DLL not found: $ServerDll"
    exit 1
}

Start-Process -FilePath "dotnet" -ArgumentList "`"$ServerDll`"" -WorkingDirectory $serverDir -WindowStyle Hidden
exit 0
