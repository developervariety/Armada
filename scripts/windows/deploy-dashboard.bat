@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%\..\..") do set "REPO_ROOT=%%~fI"
set "DASHBOARD_DIR=%REPO_ROOT%\src\Armada.Dashboard"
set "DIST_DIR=%DASHBOARD_DIR%\dist"
set "TARGET_DIR=%USERPROFILE%\.armada\dashboard"
set "STAGING_DIR=%TARGET_DIR%.staging"
set "LOCAL_BIN_DIR=%DASHBOARD_DIR%\node_modules\.bin"
set "LOCAL_TSC=%LOCAL_BIN_DIR%\tsc.cmd"
set "LOCAL_VITE=%LOCAL_BIN_DIR%\vite.cmd"

echo.
echo [deploy-dashboard] Starting dashboard build and deploy

if not exist "%DASHBOARD_DIR%\package.json" (
    echo ERROR: Dashboard project not found at %DASHBOARD_DIR%
    exit /b 1
)

pushd "%DASHBOARD_DIR%"
if not exist "node_modules" (
    echo [deploy-dashboard] Installing dependencies...
    call npm.cmd install
    if errorlevel 1 (
        popd
        exit /b 1
    )
)

echo [deploy-dashboard] Building with local dashboard toolchain...
call :build_dashboard
if errorlevel 1 (
    if exist "package-lock.json" (
        echo [deploy-dashboard] Initial build failed. Reinstalling dashboard dependencies and retrying...
        if exist "node_modules" rmdir /s /q "node_modules"
        call npm.cmd ci
        if errorlevel 1 (
            popd
            exit /b 1
        )

        echo [deploy-dashboard] Retrying build after clean install with local dashboard toolchain...
        call :build_dashboard
    )

    if errorlevel 1 (
        popd
        exit /b 1
    )
)
popd

if not exist "%DIST_DIR%\index.html" (
    echo ERROR: Dashboard build did not produce dist\index.html
    exit /b 1
)

echo [deploy-dashboard] Deploying dashboard to %TARGET_DIR%
if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
mkdir "%STAGING_DIR%" >nul 2>nul
xcopy "%DIST_DIR%\*" "%STAGING_DIR%\" /E /I /Y >nul
if errorlevel 1 (
    if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
    echo ERROR: Failed to stage dashboard for deployment to %TARGET_DIR%
    exit /b 1
)

if exist "%TARGET_DIR%" rmdir /s /q "%TARGET_DIR%"
move "%STAGING_DIR%" "%TARGET_DIR%" >nul
if errorlevel 1 (
    if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
    echo ERROR: Failed to deploy dashboard to %TARGET_DIR%
    exit /b 1
)

echo Dashboard deployed to %TARGET_DIR%
exit /b 0

:build_dashboard
if not exist "%LOCAL_TSC%" (
    echo ERROR: Dashboard TypeScript compiler not found at %LOCAL_TSC%
    exit /b 1
)

if not exist "%LOCAL_VITE%" (
    echo ERROR: Dashboard Vite CLI not found at %LOCAL_VITE%
    exit /b 1
)

call "%LOCAL_TSC%"
if errorlevel 1 exit /b 1

call "%LOCAL_VITE%" build
exit /b %ERRORLEVEL%
