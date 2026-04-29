@echo off
setlocal

set "TAG=%~1"
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..\..") do set "REPO_ROOT=%%~fI"

pushd "%REPO_ROOT%" >nul
if errorlevel 1 exit /b 1

if "%TAG%"=="" (
    echo Building jchristn77/armada-server:latest
    docker buildx build ^
        --builder cloud-jchristn77-jchristn77 ^
        --platform linux/amd64,linux/arm64/v8 ^
        -f src/Armada.Server/Dockerfile ^
        -t jchristn77/armada-server:latest ^
        --push ^
        .
) else (
    echo Building jchristn77/armada-server:latest and jchristn77/armada-server:%TAG%
    docker buildx build ^
        --builder cloud-jchristn77-jchristn77 ^
        --platform linux/amd64,linux/arm64/v8 ^
        -f src/Armada.Server/Dockerfile ^
        -t jchristn77/armada-server:latest ^
        -t jchristn77/armada-server:%TAG% ^
        --push ^
        .
)

set "EXITCODE=%ERRORLEVEL%"
popd >nul
exit /b %EXITCODE%
