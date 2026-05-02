@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1

echo.
echo [reinstall] Removing existing global Armada.Helm tool if present...
call dotnet tool uninstall --global Armada.Helm >nul 2>nul

echo.
echo [reinstall] Running fresh install...
call "%SCRIPT_DIR%\install.bat" %ARMADA_FORWARD_FRAMEWORK_ARGS%
exit /b %ERRORLEVEL%
