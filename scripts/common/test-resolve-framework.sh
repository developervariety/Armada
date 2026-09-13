#!/usr/bin/env bash
# Contract test for resolve-framework.sh. It sources the resolver in child shells and runs nothing else.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESOLVER="${SCRIPT_DIR}/resolve-framework.sh"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

[ -f "${RESOLVER}" ] || fail "resolve-framework.sh is missing"
bash -n "${RESOLVER}"

# Prints "<framework> <consumed>" for the given arguments, with ARMADA_TARGET_FRAMEWORK unset unless the
# first argument is "env=<value>".
resolve() {
    local env_value=""
    if [ "${1:-}" != "${1#env=}" ]; then
        env_value="${1#env=}"
        shift
    fi
    env -u ARMADA_TARGET_FRAMEWORK ${env_value:+ARMADA_TARGET_FRAMEWORK="$env_value"} \
        bash -c '. "$0"; armada_resolve_framework "$@"; printf "%s %s" "$ARMADA_TARGET_FRAMEWORK" "$ARMADA_FRAMEWORK_ARGS_CONSUMED"' \
        "${RESOLVER}" "$@" 2>/dev/null
}

expect() {
    local expected="$1"
    shift
    local actual
    actual="$(resolve "$@")" || fail "resolver exited non-zero for: $*"
    [ "${actual}" = "${expected}" ] || fail "for [$*] expected '${expected}' but got '${actual}'"
}

expect "net10.0 0"
expect "net8.0 2" -f net8.0
expect "net8.0 2" --framework net8.0 --insecure
expect "net8.0 1" net8.0 extra
expect "net10.0 0" server start
expect "net8.0 0" env=net8.0
expect "net10.0 2" env=net8.0 -f net10.0

if resolve -f >/dev/null; then
    fail "a missing value after -f must exit non-zero"
fi

insecure="$(env -u ARMADA_INSECURE bash -c '. "$0"; armada_resolve_framework --framework net8.0 --insecure; printf "%s" "${ARMADA_INSECURE:-}"' "${RESOLVER}" 2>/dev/null)"
[ "${insecure}" = "1" ] || fail "--insecure is still honoured when a framework is given"

for script in publish-server.sh install-mcp.sh remove-mcp.sh update.sh; do
    grep -q 'armada_resolve_framework "\$@"' "${SCRIPT_DIR}/${script}" || fail "${script} does not resolve the framework through resolve-framework.sh"
done

echo "PASS: resolve-framework.sh"
