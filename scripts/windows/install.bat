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
dotnet build "%REPO_ROOT%\src\Armada.sln" -f %ARMADA_TARGET_FRAMEWORK%
if errorlevel 1 exit /b 1

echo.
echo [install] Packing Armada.Helm for %ARMADA_TARGET_FRAMEWORK%...
dotnet pack "%REPO_ROOT%\src\Armada.Helm\Armada.Helm.csproj" -p:TargetFrameworks=%ARMADA_TARGET_FRAMEWORK% -o "%REPO_ROOT%\src\nupkg"
if errorlevel 1 exit /b 1

echo.
echo [install] Installing Armada.Helm as a global tool...
dotnet tool install --global --add-source "%REPO_ROOT%\src\nupkg" Armada.Helm

echo.
echo [install] Completed.
exit /b %ERRORLEVEL%
