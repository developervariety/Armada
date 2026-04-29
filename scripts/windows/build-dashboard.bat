@echo off
setlocal

set "TAG=%~1"
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..\..") do set "REPO_ROOT=%%~fI"

pushd "%REPO_ROOT%" >nul
if errorlevel 1 exit /b 1

if "%TAG%"=="" (
    echo Building jchristn77/armada-dashboard:latest
    docker buildx build ^
        --builder cloud-jchristn77-jchristn77 ^
        --platform linux/amd64,linux/arm64/v8 ^
        -f src/Armada.Dashboard/Dockerfile ^
        -t jchristn77/armada-dashboard:latest ^
        --push ^
        .
) else (
    echo Building jchristn77/armada-dashboard:latest and jchristn77/armada-dashboard:%TAG%
    docker buildx build ^
        --builder cloud-jchristn77-jchristn77 ^
        --platform linux/amd64,linux/arm64/v8 ^
        -f src/Armada.Dashboard/Dockerfile ^
        -t jchristn77/armada-dashboard:latest ^
        -t jchristn77/armada-dashboard:%TAG% ^
        --push ^
        .
)

set "EXITCODE=%ERRORLEVEL%"
popd >nul
exit /b %EXITCODE%
