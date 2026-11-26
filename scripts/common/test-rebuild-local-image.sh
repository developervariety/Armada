#!/usr/bin/env bash
# Exercise the Docker command boundary without a daemon, network or image build.
set -euo pipefail
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/armada-image-retention-test.XXXXXX")
trap 'rm -rf "$TEST_ROOT"' EXIT
export IMAGE_TEST_STATE="$TEST_ROOT/state"
mkdir -p "$IMAGE_TEST_STATE" "$TEST_ROOT/context"
printf 'FROM scratch\n' > "$TEST_ROOT/context/Dockerfile"
: > "$IMAGE_TEST_STATE/tags"
: > "$IMAGE_TEST_STATE/foreign-dangling"
: > "$IMAGE_TEST_STATE/untagged-parent"
cat > "$TEST_ROOT/docker" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
state=${IMAGE_TEST_STATE:?}
printf '%s\n' "$*" >> "$state/commands"
case "$1 $2" in
    'container inspect')
        case "$4" in
            '{{.State.Running}}') echo true ;;
            '{{.Image}}') echo sha256:running ;;
            *) exit 2 ;;
        esac ;;
    'image inspect')
        target=$5
        case "$4" in
            '{{.Id}}')
                if [ "$target" = demo/app:current ]; then
                    if [ -f "$state/built" ]; then echo sha256:built; else echo sha256:running; fi
                elif [[ "$target" == sha256:* ]]; then echo "$target"
                else
                    value=$(awk -v tag="$target" '$2 == tag {print $1}' "$state/tags")
                    [ -n "$value" ] || exit 1
                    echo "$value"
                fi ;;
            '{{.Created}}')
                case "$target" in
                    sha256:built) echo 2026-01-03T00:00:00Z ;;
                    sha256:running) echo 2026-01-02T00:00:00Z ;;
                    sha256:old) echo 2026-01-01T00:00:00Z ;;
                    *) exit 2 ;;
                esac ;;
            *) exit 2 ;;
        esac ;;
    'image ls')
        target=${!#}
        if [ "$target" = demo/app ]; then
            printf '%s\n' 'sha256:built current' 'sha256:running armada-retained-running-test' 'sha256:old candidate-old'
        else
            awk -v tag="$target" '$2 == tag {print $2}' "$state/tags"
        fi ;;
    'image tag') printf '%s %s\n' "$3" "$4" >> "$state/tags" ;;
    'image rm')
        [ "${!#}" = demo/app:candidate-old ] || exit 2
        : > "$state/old-removed"
        # Docker removes untagged parents unless --no-prune is supplied.
        if [ "$3" != --no-prune ]; then rm -f "$state/untagged-parent"; fi ;;
    'image prune')
        # A daemon-wide prune reaches images outside the selected repository.
        rm -f "$state/foreign-dangling" ;;
    'build --build-arg') : > "$state/built" ;;
    *) echo "Unexpected Docker request: $*" >&2; exit 2 ;;
esac
FAKE
chmod +x "$TEST_ROOT/docker"
ARMADA_DOCKER_BIN="$TEST_ROOT/docker" ARMADA_CLI_REFRESH= ARMADA_RETAINED_KEEP=1 \
    ARMADA_PRUNE_TAG_PATTERNS='candidate-*' \
    bash "$SCRIPT_DIR/rebuild-local-image.sh" demo-container demo/app:current \
    "$TEST_ROOT/context/Dockerfile" "$TEST_ROOT/context" > "$TEST_ROOT/helper.log" 2>&1
[ -f "$IMAGE_TEST_STATE/built" ] || { cat "$TEST_ROOT/helper.log"; echo 'FAIL: no build'; exit 1; }
[ -f "$IMAGE_TEST_STATE/old-removed" ] || { cat "$TEST_ROOT/helper.log"; echo 'FAIL: eligible old tag was retained'; exit 1; }
[ -f "$IMAGE_TEST_STATE/foreign-dangling" ] || { cat "$TEST_ROOT/helper.log"; echo 'FAIL: rebuild removed an unrelated dangling image'; exit 1; }
[ -f "$IMAGE_TEST_STATE/untagged-parent" ] || { cat "$TEST_ROOT/helper.log"; echo 'FAIL: removing a tag also removed an unqualified parent'; exit 1; }
echo 'PASS: image retention preserves unrelated images and parents'
