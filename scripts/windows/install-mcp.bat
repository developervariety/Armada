@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1
for %%I in ("%SCRIPT_DIR%\..\..") do set "REPO_ROOT=%%~fI"

echo.
echo [install-mcp] Configuring Armada MCP for Claude Code, Codex, Gemini, and Cursor...
dotnet run --project "%REPO_ROOT%\src\Armada.Helm" -f %ARMADA_TARGET_FRAMEWORK% -- mcp install --yes
if errorlevel 1 exit /b 1

echo.
echo [install-mcp] Completed.
exit /b 0
