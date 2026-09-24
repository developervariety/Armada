#!/usr/bin/env bash
# Behavioral tests for rebuild-local-image.sh. Docker is replaced by a local stub.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELPER="${SCRIPT_DIR}/rebuild-local-image.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

assert_contains() {
    local needle="$1"
    local file="$2"
    grep -F -- "$needle" "$file" >/dev/null || fail "${needle} is absent from ${file}"
}

assert_not_contains() {
    local needle="$1"
    local file="$2"
    if grep -F -- "$needle" "$file" >/dev/null; then
        fail "${needle} is present in ${file}"
    fi
}

FAKE_DOCKER="${TMP}/docker"
cat > "$FAKE_DOCKER" <<'FAKE'
#!/usr/bin/env bash
set -u

printf '%s\t' "$@" >> "$ARMADA_FAKE_DOCKER_LOG"
printf '\n' >> "$ARMADA_FAKE_DOCKER_LOG"

last_arg=""
for argument in "$@"; do
    last_arg="$argument"
done

if [ "${1:-}" = "container" ] && [ "${2:-}" = "inspect" ]; then
    [ "${ARMADA_FAKE_CONTAINER_MISSING:-0}" = 1 ] && exit 1
    if [ "${4:-}" = "{{.State.Running}}" ]; then
        [ "${ARMADA_FAKE_CONTAINER_STOPPED:-0}" = 1 ] && printf 'false\n' && exit 0
        printf 'true\n'
    else
        printf 'sha256:running\n'
    fi
    exit 0
fi

if [ "${1:-}" = "image" ] && [ "${2:-}" = "inspect" ]; then
    if [ "$last_arg" = "${ARMADA_FAKE_MUTABLE_TAG}" ]; then
        [ "${ARMADA_FAKE_MUTABLE_MISSING:-0}" = 1 ] && exit 1
        printf 'sha256:mutable\n'
        exit 0
    fi
    if [ "${ARMADA_FAKE_VERIFY_FAIL:-0}" = 1 ]; then
        printf 'sha256:wrong\n'
        exit 0
    fi
    if [[ "$last_arg" == *armada-retained-running-* ]]; then
        printf 'sha256:running\n'
        exit 0
    fi
    if [[ "$last_arg" == *armada-retained-tag-* ]]; then
        printf 'sha256:mutable\n'
        exit 0
    fi
    exit 1
fi

if [ "${1:-}" = "image" ] && [ "${2:-}" = "ls" ]; then
    [ "${ARMADA_FAKE_DAEMON_FAIL:-0}" = 1 ] && exit 1
    [ "${ARMADA_FAKE_COLLISION:-0}" = 1 ] && printf '%s\n' "$last_arg"
    exit 0
fi

if [ "${1:-}" = "image" ] && [ "${2:-}" = "tag" ]; then
    [ "${ARMADA_FAKE_TAG_FAIL:-0}" = 1 ] && exit 1
    exit 0
fi

if [ "${1:-}" = "build" ]; then
    [ "${ARMADA_FAKE_BUILD_FAIL:-0}" = 1 ] && exit 17
    exit 0
fi

exit 99
FAKE
chmod +x "$FAKE_DOCKER"

DOCKERFILE="${TMP}/Docker File"
CONTEXT="${TMP}/build context"
mkdir -p "$CONTEXT"
touch "$DOCKERFILE"
MUTABLE_TAG="armada:latest"

run_helper() {
    local log="$1"
    shift
    : > "$log"
    ARMADA_DOCKER_BIN="$FAKE_DOCKER" \
        ARMADA_FAKE_DOCKER_LOG="$log" \
        ARMADA_FAKE_MUTABLE_TAG="$2" \
        ARMADA_FAKE_CONTAINER_MISSING="${ARMADA_FAKE_CONTAINER_MISSING:-0}" \
        ARMADA_FAKE_CONTAINER_STOPPED="${ARMADA_FAKE_CONTAINER_STOPPED:-0}" \
        ARMADA_FAKE_MUTABLE_MISSING="${ARMADA_FAKE_MUTABLE_MISSING:-0}" \
        ARMADA_FAKE_COLLISION="${ARMADA_FAKE_COLLISION:-0}" \
        ARMADA_FAKE_DAEMON_FAIL="${ARMADA_FAKE_DAEMON_FAIL:-0}" \
        ARMADA_FAKE_VERIFY_FAIL="${ARMADA_FAKE_VERIFY_FAIL:-0}" \
        ARMADA_FAKE_TAG_FAIL="${ARMADA_FAKE_TAG_FAIL:-0}" \
        ARMADA_FAKE_BUILD_FAIL="${ARMADA_FAKE_BUILD_FAIL:-0}" \
        "$HELPER" "$@"
}

LOG_SUCCESS="${TMP}/success.log"
OUTPUT_ONE="$(run_helper "$LOG_SUCCESS" armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT")" \
    || fail "successful local rebuild returned failure"
assert_contains "retained_running_image=sha256:running" <(printf '%s\n' "$OUTPUT_ONE")
assert_contains "retained_mutable_image=sha256:mutable" <(printf '%s\n' "$OUTPUT_ONE")
assert_contains $'image\ttag\tsha256:running\t' "$LOG_SUCCESS"
assert_contains $'image\ttag\tsha256:mutable\t' "$LOG_SUCCESS"
assert_contains $'build\t--build-arg\tGIT_SHA=' "$LOG_SUCCESS"
assert_contains $'\t--file\t' "$LOG_SUCCESS"
assert_not_contains 'CLI_REFRESH=' "$LOG_SUCCESS"
assert_not_contains $'--push\t' "$LOG_SUCCESS"
assert_contains "$DOCKERFILE" "$LOG_SUCCESS"
assert_contains "$CONTEXT" "$LOG_SUCCESS"

RUNNING_TAG_ONE="$(printf '%s\n' "$OUTPUT_ONE" | sed -n 's/^retained_running_image=[^ ]* tag=//p')"
OUTPUT_TWO="$(run_helper "${TMP}/success-two.log" armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT")" \
    || fail "second successful local rebuild returned failure"
RUNNING_TAG_TWO="$(printf '%s\n' "$OUTPUT_TWO" | sed -n 's/^retained_running_image=[^ ]* tag=//p')"
[ -n "$RUNNING_TAG_ONE" ] || fail "first retention tag was not recorded"
[ -n "$RUNNING_TAG_TWO" ] || fail "second retention tag was not recorded"
[ "$RUNNING_TAG_ONE" != "$RUNNING_TAG_TWO" ] || fail "retention tag was reused"
[[ "$RUNNING_TAG_ONE" =~ ^armada:armada-retained-running-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{16}$ ]] \
    || fail "first retention tag is not uniquely dated"

FIRST_TAG_LINE="$(grep -n $'^image\ttag\t' "$LOG_SUCCESS" | head -n 1 | cut -d: -f1)"
BUILD_LINE="$(grep -n $'^build\t' "$LOG_SUCCESS" | head -n 1 | cut -d: -f1)"
[ "$FIRST_TAG_LINE" -lt "$BUILD_LINE" ] || fail "build started before image retention"

LOG_REFRESH="${TMP}/refresh.log"
ARMADA_CLI_REFRESH=1727000000 run_helper "$LOG_REFRESH" armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT" >/dev/null \
    || fail "rebuild with a CLI refresh value returned failure"
assert_contains $'--build-arg\tCLI_REFRESH=1727000000\t' "$LOG_REFRESH"

run_failure_case() {
    local name="$1"
    shift
    local log="${TMP}/${name}.log"
    if run_helper "$log" "$@" >"${TMP}/${name}.out" 2>"${TMP}/${name}.err"; then
        fail "${name} unexpectedly succeeded"
    fi
    assert_not_contains $'build\t' "$log"
}

ARMADA_CLI_REFRESH='1; rm -rf x' run_failure_case unsafe-cli-refresh \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_CONTAINER_MISSING=1 run_failure_case missing-container \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_CONTAINER_STOPPED=1 run_failure_case stopped-container \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_MUTABLE_MISSING=1 run_failure_case missing-mutable \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_COLLISION=1 run_failure_case retention-collision \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_DAEMON_FAIL=1 run_failure_case retention-inspect-failure \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_TAG_FAIL=1 run_failure_case tag-failure \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

ARMADA_FAKE_VERIFY_FAIL=1 run_failure_case retained-inspect-failure \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT"

LOG_BUILD_FAILURE="${TMP}/build-failure.log"
if ARMADA_FAKE_BUILD_FAIL=1 run_helper "$LOG_BUILD_FAILURE" \
    armada-server "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT" \
    >"${TMP}/build-failure.out" 2>"${TMP}/build-failure.err"; then
    fail "build failure unexpectedly succeeded"
fi
assert_contains $'image\ttag\tsha256:running\t' "$LOG_BUILD_FAILURE"
assert_contains $'image\ttag\tsha256:mutable\t' "$LOG_BUILD_FAILURE"
assert_contains $'build\t' "$LOG_BUILD_FAILURE"

SENTINEL="${TMP}/injection-sentinel"
LOG_UNSAFE="${TMP}/unsafe.log"
if run_helper "$LOG_UNSAFE" 'bad;touch' "$MUTABLE_TAG" "$DOCKERFILE" "$CONTEXT" \
    >"${TMP}/unsafe.out" 2>"${TMP}/unsafe.err"; then
    fail "unsafe container name unexpectedly succeeded"
fi
[ ! -e "$SENTINEL" ] || fail "unsafe container input executed shell code"
[ ! -s "$LOG_UNSAFE" ] || fail "Docker was called for unsafe container input"

LOG_UNSAFE_TAG="${TMP}/unsafe-tag.log"
if run_helper "$LOG_UNSAFE_TAG" armada-server 'local/armada-server:latest;touch' "$DOCKERFILE" "$CONTEXT" \
    >"${TMP}/unsafe-tag.out" 2>"${TMP}/unsafe-tag.err"; then
    fail "unsafe image tag unexpectedly succeeded"
fi
[ ! -s "$LOG_UNSAFE_TAG" ] || fail "Docker was called for unsafe image tag input"

echo "PASS: rebuild-local-image.sh"
