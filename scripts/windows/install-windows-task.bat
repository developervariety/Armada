@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1
set "STARTUP_VALUE_NAME=ArmadaAdmiral"
set "STARTUP_DISPLAY_NAME=Armada Admiral"
set "START_SCRIPT=%SCRIPT_DIR%\start-armada-server.ps1"
set "STOP_SCRIPT=%SCRIPT_DIR%\stop-armada-server.ps1"
set "SERVER_EXE=%USERPROFILE%\.armada\bin\Armada.Server.exe"

where powershell >nul 2>nul
if errorlevel 1 (
    echo ERROR: powershell.exe was not found on PATH.
    exit /b 1
)

echo.
echo [install-windows-task] Using target framework %ARMADA_TARGET_FRAMEWORK%...
echo [install-windows-task] Publishing Armada.Server...
call "%SCRIPT_DIR%\publish-server.bat" --framework %ARMADA_TARGET_FRAMEWORK%
if errorlevel 1 exit /b 1

if not exist "%SERVER_EXE%" (
    echo ERROR: Published server executable not found at %SERVER_EXE%
    exit /b 1
)

echo.
echo [install-windows-task] Registering current-user startup entry "%STARTUP_DISPLAY_NAME%"...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$startupPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';" ^
    "$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe';" ^
    "if (-not (Test-Path -LiteralPath $powerShellExe)) { $powerShellExe = 'powershell.exe' };" ^
    "$startScript = [System.IO.Path]::GetFullPath($env:START_SCRIPT);" ^
    "$quote = [char]34;" ^
    "$command = $quote + $powerShellExe + $quote + ' -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ' + $quote + $startScript + $quote;" ^
    "New-Item -Path $startupPath -Force | Out-Null;" ^
    "New-ItemProperty -Path $startupPath -Name $env:STARTUP_VALUE_NAME -Value $command -PropertyType String -Force | Out-Null"
if errorlevel 1 exit /b 1

echo.
echo [install-windows-task] Starting Armada.Server in the current user session...
powershell -NoProfile -ExecutionPolicy Bypass -File "%STOP_SCRIPT%" >nul 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%START_SCRIPT%"
if errorlevel 1 exit /b 1

call "%SCRIPT_DIR%\healthcheck-server.bat"
if errorlevel 1 exit /b 1

echo.
echo [install-windows-task] Completed.
exit /b 0
