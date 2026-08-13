@echo off
setlocal enabledelayedexpansion

rem =====================================================================
rem factory-reset.bat -- wipe Armada back to a factory-fresh state.
rem
rem Stops every Armada.Server, then deletes the database and all runtime
rem state under %USERPROFILE%\.armada so the next start comes up empty as if
rem freshly deployed. By default it KEEPS the deployed server bin and the
rem dashboard so the managed deployment still runs; pass --all to remove
rem those too and wipe the entire %USERPROFILE%\.armada directory.
rem
rem Flags:
rem   -y / --yes / --force   skip the confirmation prompt
rem   --all                  also delete bin and dashboard entire directory
rem
rem NOTE: this regenerates the API key on next start; MCP clients and the CLI
rem that stored the old key must be reconfigured.
rem =====================================================================

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ARMADA_DIR=%USERPROFILE%\.armada"
set "STOP_SCRIPT=%SCRIPT_DIR%\stop-armada-server.ps1"

set "FORCE="
set "WIPE_ALL="
for %%A in (%*) do (
    if /I "%%~A"=="-y" set "FORCE=1"
    if /I "%%~A"=="--yes" set "FORCE=1"
    if /I "%%~A"=="--force" set "FORCE=1"
    if /I "%%~A"=="--all" set "WIPE_ALL=1"
)

if not exist "%ARMADA_DIR%" (
    echo [factory-reset] Nothing to reset: %ARMADA_DIR% does not exist.
    exit /b 0
)

if not defined FORCE (
    echo.
    echo WARNING: this permanently DELETES the Armada database and runtime state under:
    echo     %ARMADA_DIR%
    echo   - armada.db: all fleets, vessels, captains, missions, voyages, jobs, and more
    echo   - docks: git worktrees; repos: clones; logs; settings.json
    if defined WIPE_ALL echo   - --all: ALSO the deployed server bin and dashboard
    echo.
    set /p "CONFIRM=Type YES to continue: "
    if /I not "!CONFIRM!"=="YES" (
        echo [factory-reset] Aborted.
        exit /b 1
    )
)

echo.
echo [factory-reset] Stopping every Armada.Server process...
if exist "%STOP_SCRIPT%" powershell -NoProfile -ExecutionPolicy Bypass -File "%STOP_SCRIPT%"
taskkill /F /IM Armada.Server.exe >nul 2>nul

echo [factory-reset] Deleting database and runtime state...
del /q "%ARMADA_DIR%\armada.db" >nul 2>nul
del /q "%ARMADA_DIR%\armada.db-shm" >nul 2>nul
del /q "%ARMADA_DIR%\armada.db-wal" >nul 2>nul
del /q "%ARMADA_DIR%\crash.log" >nul 2>nul
del /q "%ARMADA_DIR%\settings.json" >nul 2>nul
if exist "%ARMADA_DIR%\docks" rmdir /s /q "%ARMADA_DIR%\docks"
if exist "%ARMADA_DIR%\repos" rmdir /s /q "%ARMADA_DIR%\repos"
if exist "%ARMADA_DIR%\logs" rmdir /s /q "%ARMADA_DIR%\logs"

if defined WIPE_ALL (
    echo [factory-reset] Removing deployed server bin and dashboard...
    if exist "%ARMADA_DIR%\bin" rmdir /s /q "%ARMADA_DIR%\bin"
    if exist "%ARMADA_DIR%\dashboard" rmdir /s /q "%ARMADA_DIR%\dashboard"
)

echo.
echo [factory-reset] Done. Armada state wiped at %ARMADA_DIR%.
if defined WIPE_ALL (
    echo Reinstall to redeploy: scripts\windows\install-windows-task.bat
) else (
    echo Start the server to come up factory-fresh: scripts\windows\update-windows-task.bat
    echo   or it will start automatically on next login.
)
exit /b 0
