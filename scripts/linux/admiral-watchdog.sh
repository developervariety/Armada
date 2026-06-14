#!/bin/sh
set -eu

WORKING_DIRECTORY=""
ADMIRAL_PID=""
SERVER_DLL=""
SHUTDOWN_WAIT_SECONDS=120

while [ $# -gt 0 ]; do
    case "$1" in
        --working-directory)
            WORKING_DIRECTORY="$2"
            shift 2
            ;;
        --admiral-pid)
            ADMIRAL_PID="$2"
            shift 2
            ;;
        --server-dll)
            SERVER_DLL="$2"
            shift 2
            ;;
        --shutdown-wait-seconds)
            SHUTDOWN_WAIT_SECONDS="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

if [ -z "$WORKING_DIRECTORY" ] || [ -z "$ADMIRAL_PID" ] || [ -z "$SERVER_DLL" ]; then
    echo "Missing required arguments." >&2
    exit 1
fi

cd "$WORKING_DIRECTORY"

elapsed=0
while kill -0 "$ADMIRAL_PID" 2>/dev/null && [ "$elapsed" -lt "$SHUTDOWN_WAIT_SECONDS" ]; do
    sleep 1
    elapsed=$((elapsed + 1))
done

if kill -0 "$ADMIRAL_PID" 2>/dev/null; then
    kill -9 "$ADMIRAL_PID" 2>/dev/null || true
fi

if [ ! -f "$SERVER_DLL" ]; then
    echo "Server DLL not found: $SERVER_DLL" >&2
    exit 1
fi

SERVER_DIR=$(dirname "$SERVER_DLL")
nohup dotnet "$SERVER_DLL" >/dev/null 2>&1 &
exit 0
