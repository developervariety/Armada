@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
call "%SCRIPT_DIR%\resolve-framework.bat" %*
if errorlevel 1 exit /b 1
for %%I in ("%SCRIPT_DIR%\..\..") do set "REPO_ROOT=%%~fI"

echo.
echo [install] Deploying dashboard...
call "%SCRIPT_DIR%\deploy-dashboard.bat"
if errorlevel 1 exit /b 1

echo.
echo [install] Building Armada solution for %ARMADA_TARGET_FRAMEWORK%...
call dotnet build "%REPO_ROOT%\src\Armada.sln" %ARMADA_DOTNET_FRAMEWORK_ARGS%
if errorlevel 1 exit /b 1

echo.
echo [install] Packing Armada.Helm for %ARMADA_TARGET_FRAMEWORK%...
call dotnet pack "%REPO_ROOT%\src\Armada.Helm\Armada.Helm.csproj" %ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS% -o "%REPO_ROOT%\src\nupkg"
if errorlevel 1 exit /b 1

echo.
echo [install] Installing Armada.Helm as a global tool...
call dotnet tool install --global --add-source "%REPO_ROOT%\src\nupkg" Armada.Helm

echo.
echo [install] Completed.
exit /b %ERRORLEVEL%
