@echo off
setlocal

set "BACKEND=%~1"
if "%BACKEND%"=="" set "BACKEND=all"
set "SCRIPT_DIR=%~dp0"

echo Armada v0.8.0 adds normalized backlog/objective and refinement persistence.
echo.
echo Before applying any manual SQL:
echo - Back up the database.
echo - Confirm the target Armada instance is stopped or otherwise isolated.
echo - Verify whether automatic startup migration is acceptable instead.
echo.
echo Notes:
echo - Armada.Server applies these schema changes automatically on first startup after upgrade.
echo - Use this script when you need a controlled DBA-managed pre-stage or reviewable SQL handoff.
echo - SQLite advances to schema version 43; PostgreSQL, MySQL, and SQL Server advance to schema version 42.
echo.

if /I "%BACKEND%"=="sqlite" goto sqlite
if /I "%BACKEND%"=="postgresql" goto postgresql
if /I "%BACKEND%"=="postgres" goto postgresql
if /I "%BACKEND%"=="pgsql" goto postgresql
if /I "%BACKEND%"=="sqlserver" goto sqlserver
if /I "%BACKEND%"=="mssql" goto sqlserver
if /I "%BACKEND%"=="mysql" goto mysql
if /I "%BACKEND%"=="all" goto all

echo Usage: %~nx0 [sqlite^|postgresql^|sqlserver^|mysql^|all]
exit /b 1

:all
call :print_file SQLite migrate_v0.7.0_to_v0.8.0_sqlite.sql
echo.
call :print_file PostgreSQL migrate_v0.7.0_to_v0.8.0_postgresql.sql
echo.
call :print_file SQLServer migrate_v0.7.0_to_v0.8.0_sqlserver.sql
echo.
call :print_file MySQL migrate_v0.7.0_to_v0.8.0_mysql.sql
exit /b 0

:sqlite
call :print_file SQLite migrate_v0.7.0_to_v0.8.0_sqlite.sql
exit /b 0

:postgresql
call :print_file PostgreSQL migrate_v0.7.0_to_v0.8.0_postgresql.sql
exit /b 0

:sqlserver
call :print_file SQLServer migrate_v0.7.0_to_v0.8.0_sqlserver.sql
exit /b 0

:mysql
call :print_file MySQL migrate_v0.7.0_to_v0.8.0_mysql.sql
exit /b 0

:print_file
echo -- %~1
type "%SCRIPT_DIR%%~2"
exit /b 0
