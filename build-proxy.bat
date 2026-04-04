@echo off

set TAG=%~1

if "%TAG%"=="" (
    echo Building jchristn77/armada-proxy:latest
    docker buildx build ^
        --platform linux/amd64 ^
        -f src/Armada.Proxy/Dockerfile ^
        -t jchristn77/armada-proxy:latest ^
        --push ^
        .
) else (
    echo Building jchristn77/armada-proxy:latest and jchristn77/armada-proxy:%TAG%
    docker buildx build ^
        --platform linux/amd64 ^
        -f src/Armada.Proxy/Dockerfile ^
        -t jchristn77/armada-proxy:latest ^
        -t jchristn77/armada-proxy:%TAG% ^
        --push ^
        .
)
