@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1
for %%I in ("%SCRIPT_DIR%\..\..") do set "REPO_ROOT=%%~fI"
set "HELM_DLL=%REPO_ROOT%\src\Armada.Helm\bin\Debug\%ARMADA_TARGET_FRAMEWORK%\Armada.Helm.dll"

echo.
echo [update] Stopping repo-backed Armada MCP stdio hosts if they are running...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%\stop-repo-mcp-hosts.ps1" -RepoRoot "%REPO_ROOT%"
if errorlevel 1 exit /b 1

echo.
echo [update] Stopping Armada server if it is running...
call :run_helm server stop

echo.
echo [update] Reinstalling Armada tool and redeploying dashboard...
call "%SCRIPT_DIR%\reinstall.bat" --framework %ARMADA_TARGET_FRAMEWORK%
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

dotnet run --project "%REPO_ROOT%\src\Armada.Helm" -f %ARMADA_TARGET_FRAMEWORK% -- %*
exit /b %ERRORLEVEL%
