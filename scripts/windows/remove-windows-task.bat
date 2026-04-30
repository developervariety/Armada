@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1
set "STARTUP_VALUE_NAME=ArmadaAdmiral"
set "STOP_SCRIPT=%SCRIPT_DIR%\stop-armada-server.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "if ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $env:STARTUP_VALUE_NAME -ErrorAction SilentlyContinue) -eq $null) { exit 1 } else { exit 0 }" >nul
if errorlevel 1 (
    echo.
    echo [remove-windows-task] Current-user startup entry "%STARTUP_VALUE_NAME%" is not installed.
    exit /b 0
)

echo.
echo [remove-windows-task] Stopping Armada.Server...
powershell -NoProfile -ExecutionPolicy Bypass -File "%STOP_SCRIPT%" >nul 2>nul

echo.
echo [remove-windows-task] Removing current-user startup entry...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $env:STARTUP_VALUE_NAME -ErrorAction Stop"
if errorlevel 1 exit /b 1

echo.
echo [remove-windows-task] Completed.
exit /b 0
