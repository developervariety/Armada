@echo off
setlocal
REM Delegates to the common bash script (requires Git Bash / WSL bash on PATH),
REM which orchestrates the Docker containers and runs the full Database suite
REM against every provider.
set "SCRIPT_DIR=%~dp0"
where bash >nul 2>nul
if errorlevel 1 (
  echo ERROR: 'bash' not found on PATH. Install Git for Windows ^(Git Bash^) or run
  echo the suite manually, e.g.:
  echo   dotnet run --project src\Test.Automated --framework net10.0 -- --db-type postgresql --db-host 127.0.0.1 --db-port 5432 --db-user postgres --db-pass ^<pw^> --db-name armada_test --suites Database
  exit /b 1
)
bash "%SCRIPT_DIR%..\common\run-db-parity-tests.sh" %*
exit /b %errorlevel%
