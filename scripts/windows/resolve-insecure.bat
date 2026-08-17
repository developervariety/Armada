@echo off
REM Scans all arguments for an "ignore TLS certificate" flag. When present, disables strict TLS
REM certificate validation for npm/Node for the rest of this run so package restore works behind an
REM SSL-inspecting corporate proxy that injects a self-signed root certificate. The exported
REM environment variables propagate to every child process and sub-script.
REM
REM Recognized flags (anywhere on the command line): -k, --insecure, --no-strict-ssl, --ignore-cert-errors
REM
REM NOTE: npm ships its own CA bundle (separate from the Windows certificate store), which is why an
REM SSL-inspecting proxy breaks npm even when dotnet/NuGet (which use the OS store) work fine.

:scan
if "%~1"=="" goto :end
if /I "%~1"=="-k" goto :enable
if /I "%~1"=="--insecure" goto :enable
if /I "%~1"=="--no-strict-ssl" goto :enable
if /I "%~1"=="--ignore-cert-errors" goto :enable
shift
goto :scan

:enable
set "ARMADA_INSECURE=1"
set "NODE_TLS_REJECT_UNAUTHORIZED=0"
set "npm_config_strict_ssl=false"
echo [insecure] TLS certificate validation disabled for this run ^(npm/Node^).

:end
exit /b 0
