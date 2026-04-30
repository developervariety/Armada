@echo off
setlocal

set "FRAMEWORK=%ARMADA_TARGET_FRAMEWORK%"
if "%FRAMEWORK%"=="" set "FRAMEWORK=net10.0"

if /I "%~1"=="-f" (
    if "%~2"=="" (
        echo ERROR: Missing framework value after -f.
        exit /b 1
    )

    set "FRAMEWORK=%~2"
) else if /I "%~1"=="--framework" (
    if "%~2"=="" (
        echo ERROR: Missing framework value after --framework.
        exit /b 1
    )

    set "FRAMEWORK=%~2"
) else if not "%~1"=="" (
    set "FRAMEWORK=%~1"
)

endlocal & set "ARMADA_TARGET_FRAMEWORK=%FRAMEWORK%" & exit /b 0
