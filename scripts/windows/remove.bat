@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1

echo.
echo [remove] Uninstalling global Armada.Helm tool if present...
dotnet tool uninstall --global Armada.Helm >nul 2>nul

if exist "%USERPROFILE%\.armada\dashboard" (
    echo.
    echo [remove] Removing deployed dashboard from %USERPROFILE%\.armada\dashboard
    rmdir /s /q "%USERPROFILE%\.armada\dashboard"
)

echo.
echo [remove] Completed.
exit /b 0
