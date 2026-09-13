#!/usr/bin/env bash
# Resolves the target .NET framework for Armada's POSIX scripts, matching scripts/windows/resolve-framework.bat.
#
# Source this file, then call: armada_resolve_framework "$@"
#
# Sets and exports ARMADA_TARGET_FRAMEWORK, ARMADA_FORWARD_FRAMEWORK_ARGS, ARMADA_DOTNET_FRAMEWORK_ARGS and
# ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS. Sets ARMADA_FRAMEWORK_ARGS_CONSUMED to the number of leading arguments it
# used, so a caller that forwards the rest can shift them off first.
#
# Priority:
#   1. -f <framework> or --framework <framework>
#   2. a leading bare framework moniker such as net8.0
#   3. the ARMADA_TARGET_FRAMEWORK environment variable
#   4. net10.0
#
# Only a leading token that starts with "net" is read as a bare framework, so a forwarded command such as
# "server start" is never taken as one. The same arguments are also passed to resolve-insecure.sh.
armada_resolve_framework() {
    ARMADA_TARGET_FRAMEWORK="${ARMADA_TARGET_FRAMEWORK:-net10.0}"
    ARMADA_FRAMEWORK_ARGS_CONSUMED=0

    case "${1:-}" in
        -f|--framework)
            if [ -z "${2:-}" ]; then
                echo "ERROR: Missing framework value after $1." >&2
                exit 1
            fi
            ARMADA_TARGET_FRAMEWORK="$2"
            ARMADA_FRAMEWORK_ARGS_CONSUMED=2
            ;;
        net*)
            ARMADA_TARGET_FRAMEWORK="$1"
            ARMADA_FRAMEWORK_ARGS_CONSUMED=1
            ;;
    esac

    export ARMADA_TARGET_FRAMEWORK
    export ARMADA_FORWARD_FRAMEWORK_ARGS="--framework ${ARMADA_TARGET_FRAMEWORK}"
    export ARMADA_DOTNET_FRAMEWORK_ARGS="--framework ${ARMADA_TARGET_FRAMEWORK} -p:TargetFramework=${ARMADA_TARGET_FRAMEWORK} -p:TargetFrameworks=${ARMADA_TARGET_FRAMEWORK}"
    export ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS="-p:TargetFramework=${ARMADA_TARGET_FRAMEWORK} -p:TargetFrameworks=${ARMADA_TARGET_FRAMEWORK}"

    # shellcheck source=resolve-insecure.sh
    . "$(dirname "${BASH_SOURCE[0]}")/resolve-insecure.sh"
    armada_resolve_insecure "$@"
}
