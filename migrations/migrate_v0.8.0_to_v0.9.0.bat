@echo off
setlocal enabledelayedexpansion
set "BACKEND=%~1"
if "%BACKEND%"=="" set "BACKEND=all"
set "SCRIPT_DIR=%~dp0"

echo Armada v0.9.0 is a reliability release. It adds:
echo - Dock lifecycle state and a renewable lease (docks.state / lease_expires_utc / owner_token).
echo - Process-liveness telemetry separate from the output heartbeat (captains.last_process_alive_utc).
echo - A review-stage deadline for the review-timeout watchdog (missions.review_deadline_utc).
echo - Bounded merge-queue retries and stuck-entry recovery (merge_entries.retry_count / lease_expires_utc).
echo - A durable coordination-lease table for multi-instance-safe mutual exclusion (coordination_leases).
echo.
echo Before applying any manual SQL: back up the database and stop the target instance.
echo Armada.Server applies these changes automatically on first startup after upgrade.
echo All backends advance to schema version 44.
echo.

if /I "%BACKEND%"=="sqlite"     ( call :emit "SQLite" "migrate_v0.8.0_to_v0.9.0_sqlite.sql" & goto :eof )
if /I "%BACKEND%"=="postgresql" ( call :emit "PostgreSQL" "migrate_v0.8.0_to_v0.9.0_postgresql.sql" & goto :eof )
if /I "%BACKEND%"=="mysql"      ( call :emit "MySQL" "migrate_v0.8.0_to_v0.9.0_mysql.sql" & goto :eof )
if /I "%BACKEND%"=="sqlserver"  ( call :emit "SQL Server" "migrate_v0.8.0_to_v0.9.0_sqlserver.sql" & goto :eof )
if /I "%BACKEND%"=="all" (
    call :emit "SQLite" "migrate_v0.8.0_to_v0.9.0_sqlite.sql"
    call :emit "PostgreSQL" "migrate_v0.8.0_to_v0.9.0_postgresql.sql"
    call :emit "MySQL" "migrate_v0.8.0_to_v0.9.0_mysql.sql"
    call :emit "SQL Server" "migrate_v0.8.0_to_v0.9.0_sqlserver.sql"
    goto :eof
)

echo Usage: %~nx0 [all^|sqlite^|postgresql^|mysql^|sqlserver] 1>&2
exit /b 1

:emit
echo -- %~1
type "%SCRIPT_DIR%%~2"
echo.
goto :eof
