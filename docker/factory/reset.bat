@echo off
echo ========================================
echo  Armada Factory Reset
echo ========================================
echo.
echo This will delete local SQLite database files and log files.
echo Configuration (docker\server\armada.json) will be preserved.
echo External databases referenced by docker\server\armada.json are not modified.
echo.
set /p confirm="Type 'RESET' to confirm: "
if /i not "%confirm%"=="RESET" (
    echo Cancelled.
    exit /b 1
)
echo.
echo [1/3] Stopping containers...
docker compose down
echo.
echo [2/3] Deleting local database files...
if exist "..\armada\db" (
    del /q "..\armada\db\*" 2>nul
    echo   Local database files deleted.
) else (
    echo   No database directory found.
)
echo.
echo [3/3] Deleting log files...
if exist "..\armada\logs" (
    del /q "..\armada\logs\*" 2>nul
    echo   Log files deleted.
) else (
    echo   No logs directory found.
)
echo.
echo ========================================
echo  Factory reset complete.
echo  Run 'docker compose up -d' to restart.
echo ========================================
