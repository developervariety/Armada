@echo off
setlocal

rem =====================================================================
rem update-windows-task.bat -- updates the WINDOWS AUTO-START install
rem (see install-windows-task.bat). Requires the HKCU Run entry
rem "ArmadaAdmiral"; stops the running server, republishes the
rem self-contained server to %USERPROFILE%\.armada\bin, and re-registers
rem the startup entry. This is NOT the same as update.bat, which updates a
rem global-tool / manual install (Armada.Helm dotnet tool, foreground
rem server). Use the update script that matches how you installed; they are
rem not interchangeable.
rem =====================================================================

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1
set "STARTUP_VALUE_NAME=ArmadaAdmiral"
set "STOP_SCRIPT=%SCRIPT_DIR%\stop-armada-server.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "if ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $env:STARTUP_VALUE_NAME -ErrorAction SilentlyContinue) -eq $null) { exit 1 } else { exit 0 }" >nul
if errorlevel 1 (
    echo ERROR: Current-user startup entry "%STARTUP_VALUE_NAME%" is not installed.
    echo Run install-windows-task.bat first.
    exit /b 1
)

echo.
echo [update-windows-task] Stopping every running Armada.Server process...
rem PowerShell helper stops managed and dotnet-hosted instances and waits for exit.
powershell -NoProfile -ExecutionPolicy Bypass -File "%STOP_SCRIPT%"
rem Batch-level guarantee: force-kill any Armada.Server.exe by image name regardless of path,
rem so a server launched straight out of the repo bin cannot survive and hold the port/DLL locks.
taskkill /F /IM Armada.Server.exe >nul 2>nul

echo [update-windows-task] Using target framework %ARMADA_TARGET_FRAMEWORK%...
call "%SCRIPT_DIR%\install-windows-task.bat" %ARMADA_FORWARD_FRAMEWORK_ARGS%
exit /b %ERRORLEVEL%
