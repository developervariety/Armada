@echo off
setlocal

set "BASE_URL=%~1"
if "%BASE_URL%"=="" set "BASE_URL=%ARMADA_BASE_URL%"
if "%BASE_URL%"=="" set "BASE_URL=http://localhost:7890"
if "%BASE_URL:~-1%"=="/" set "BASE_URL=%BASE_URL:~0,-1%"
set "HEALTH_URL=%BASE_URL%/api/v1/status/health"

echo.
echo [healthcheck-server] Waiting for %HEALTH_URL%...

for /l %%I in (1,1,30) do (
    powershell -NoProfile -Command ^
        "try { $resp = Invoke-WebRequest -UseBasicParsing -Uri '%HEALTH_URL%' -TimeoutSec 3; if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>nul
    if not errorlevel 1 (
        echo [healthcheck-server] Healthy.
        exit /b 0
    )

    timeout /t 1 /nobreak >nul
)

echo ERROR: Armada server did not become healthy at %HEALTH_URL%
exit /b 1
