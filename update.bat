@echo off
setlocal

for %%I in ("%~dp0.") do set "SCRIPT_DIR=%%~fI"
set "HELM_DLL=%SCRIPT_DIR%\src\Armada.Helm\bin\Debug\net10.0\Armada.Helm.dll"

echo.
echo [update] Stopping repo-backed Armada MCP stdio hosts if they are running...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%\scripts\stop-repo-mcp-hosts.ps1" -RepoRoot "%SCRIPT_DIR%"
if errorlevel 1 exit /b 1

echo.
echo [update] Stopping Armada server if it is running...
call :run_helm server stop

echo.
echo [update] Reinstalling Armada tool and redeploying dashboard...
call "%SCRIPT_DIR%\reinstall.bat"
if errorlevel 1 exit /b 1

echo.
echo [update] Starting Armada server...
call :run_helm server start
exit /b %ERRORLEVEL%

:run_helm
where armada >nul 2>nul
if not errorlevel 1 (
  armada %*
  exit /b %ERRORLEVEL%
)

if exist "%HELM_DLL%" (
  dotnet "%HELM_DLL%" %*
  exit /b %ERRORLEVEL%
)

dotnet run --project "%SCRIPT_DIR%\src\Armada.Helm" -f net10.0 -- %*
exit /b %ERRORLEVEL%
