#!/usr/bin/env bash
# Rebuild one local Docker image only after immutable rollback tags exist.
set -euo pipefail

usage() {
    cat >&2 <<'USAGE'
Usage: rebuild-local-image.sh <running-container> <mutable-image-tag> <dockerfile> <context>

The helper records and retains the image used by the running container and the
current mutable tag before it runs a local `docker build`. Set
ARMADA_DOCKER_BIN to inject a Docker-compatible test double. Set
ARMADA_CLI_REFRESH to a number (for example `date +%s`) to pass it as the
Dockerfile's CLI_REFRESH build argument, which re-installs the agent CLIs at
their latest versions.

After a successful build the helper removes retention sets older than the
newest ARMADA_RETAINED_KEEP (default 3). A set is removed by tag, so an image a
container still runs, or that another tag names, stays on disk.
USAGE
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

is_safe_scalar() {
    local value="$1"
    [ -n "$value" ] || return 1
    case "$value" in
        -*|*[!A-Za-z0-9._:/-]*) return 1 ;;
    esac
}

if [ "$#" -ne 4 ]; then
    usage
    exit 2
fi

CLI_REFRESH_VALUE="${ARMADA_CLI_REFRESH:-}"
case "$CLI_REFRESH_VALUE" in
    *[!0-9]*) die "ARMADA_CLI_REFRESH must contain digits only (for example the output of date +%s)" ;;
esac

RETAINED_KEEP="${ARMADA_RETAINED_KEEP:-3}"
case "$RETAINED_KEEP" in
    ''|*[!0-9]*) die "ARMADA_RETAINED_KEEP must be a whole number of at least 1" ;;
esac
[ "$RETAINED_KEEP" -ge 1 ] || die "ARMADA_RETAINED_KEEP must be a whole number of at least 1"

CONTAINER="$1"
MUTABLE_TAG="$2"
DOCKERFILE="$3"
CONTEXT="$4"
DOCKER_BIN="${ARMADA_DOCKER_BIN:-docker}"

is_safe_scalar "$CONTAINER" || die "running container name is invalid"
is_safe_scalar "$MUTABLE_TAG" || die "mutable image tag is invalid"
case "$MUTABLE_TAG" in
    *:*) ;;
    *) die "mutable image tag must include a repository and tag" ;;
esac
[ -f "$DOCKERFILE" ] || die "Dockerfile does not exist: $DOCKERFILE"
[ -d "$CONTEXT" ] || die "build context does not exist: $CONTEXT"

MUTABLE_REPOSITORY="${MUTABLE_TAG%:*}"
MUTABLE_IMAGE_NAME="${MUTABLE_TAG##*:}"
[ -n "$MUTABLE_REPOSITORY" ] || die "mutable image repository is empty"
[ -n "$MUTABLE_IMAGE_NAME" ] || die "mutable image tag name is empty"
case "$MUTABLE_IMAGE_NAME" in
    *[:/@]*|[-.]*|*[!A-Za-z0-9_.-]*) die "mutable image tag name is invalid" ;;
esac
case "${CONTEXT##*/}" in
    -*) die "build context name is invalid" ;;
esac

DOCKER=("$DOCKER_BIN")

CONTAINER_RUNNING="$("${DOCKER[@]}" container inspect --format '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" \
    || die "running container inspect failed"
[ "$CONTAINER_RUNNING" = "true" ] || die "container is not running"
RUNNING_IMAGE_ID="$("${DOCKER[@]}" container inspect --format '{{.Image}}' "$CONTAINER" 2>/dev/null)" \
    || die "running container image inspect failed"
case "$RUNNING_IMAGE_ID" in
    sha256:*) ;;
    *) die "running container has no immutable image ID" ;;
esac

MUTABLE_IMAGE_ID="$("${DOCKER[@]}" image inspect --format '{{.Id}}' "$MUTABLE_TAG" 2>/dev/null)" \
    || die "mutable image tag inspect failed"
case "$MUTABLE_IMAGE_ID" in
    sha256:*) ;;
    *) die "mutable image tag has no immutable image ID" ;;
esac

RETENTION_TIMESTAMP="$(date -u '+%Y%m%dT%H%M%SZ')" \
    || die "retention timestamp failed"
case "$RETENTION_TIMESTAMP" in
    [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]T[0-9][0-9][0-9][0-9][0-9][0-9]Z) ;;
    *) die "retention timestamp is invalid" ;;
esac

RETENTION_NONCE="$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')" \
    || die "retention nonce failed"
case "$RETENTION_NONCE" in
    [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) ;;
    *) die "retention nonce is invalid" ;;
esac

RUNNING_RETENTION_TAG="${MUTABLE_REPOSITORY}:armada-retained-running-${RETENTION_TIMESTAMP}-${RETENTION_NONCE}"
MUTABLE_RETENTION_TAG="${MUTABLE_REPOSITORY}:armada-retained-tag-${RETENTION_TIMESTAMP}-${RETENTION_NONCE}"
RUNNING_RETENTION_NAME="${RUNNING_RETENTION_TAG##*:}"
MUTABLE_RETENTION_NAME="${MUTABLE_RETENTION_TAG##*:}"
[ "${#RUNNING_RETENTION_NAME}" -le 128 ] || die "running retention tag is too long"
[ "${#MUTABLE_RETENTION_NAME}" -le 128 ] || die "mutable retention tag is too long"

retention_tag_is_absent() {
    local tag="$1"
    local listed
    listed="$("${DOCKER[@]}" image ls --no-trunc --format '{{.Repository}}:{{.Tag}}' "$tag" 2>/dev/null)" \
        || die "retention tag collision check failed"
    if [ -n "$listed" ]; then
        [ "$listed" = "$tag" ] && die "retention tag already exists; refusing overwrite"
        die "retention tag collision check returned an unexpected tag"
    fi
}

retention_tag_is_absent "$RUNNING_RETENTION_TAG"
retention_tag_is_absent "$MUTABLE_RETENTION_TAG"

verify_retention_tag() {
    local source_id="$1"
    local tag="$2"
    local retained_id
    retained_id="$("${DOCKER[@]}" image inspect --format '{{.Id}}' "$tag" 2>/dev/null)" \
        || die "retained image verification failed"
    [ "$retained_id" = "$source_id" ] || die "retained image ID verification failed"
}

# Keep both references before the build. If a later tag or the build fails,
# these references remain available for operator rollback.
"${DOCKER[@]}" image tag "$RUNNING_IMAGE_ID" "$RUNNING_RETENTION_TAG" \
    || die "running image retention tag failed"
verify_retention_tag "$RUNNING_IMAGE_ID" "$RUNNING_RETENTION_TAG"
"${DOCKER[@]}" image tag "$MUTABLE_IMAGE_ID" "$MUTABLE_RETENTION_TAG" \
    || die "mutable image retention tag failed"
verify_retention_tag "$MUTABLE_IMAGE_ID" "$MUTABLE_RETENTION_TAG"

echo "retained_running_image=${RUNNING_IMAGE_ID} tag=${RUNNING_RETENTION_TAG}"
echo "retained_mutable_image=${MUTABLE_IMAGE_ID} tag=${MUTABLE_RETENTION_TAG}"
echo "building_mutable_tag=${MUTABLE_TAG}"

# Fixed arguments and quoted arrays prevent image names or paths from becoming
# shell code. No push flag is accepted: this helper is for local rebuilds.
# Pass the context's commit so the image can embed its build commit; the
# Dockerfile cannot run git itself. An empty value (no git, detached, or a
# non-repository context) keeps the unknown-commit behaviour.
BUILD_COMMIT="$(git -C "$CONTEXT" rev-parse HEAD 2>/dev/null || true)"
BUILD_ARGS=(--build-arg "GIT_SHA=${BUILD_COMMIT}")
if [ -n "$CLI_REFRESH_VALUE" ]; then
    BUILD_ARGS+=(--build-arg "CLI_REFRESH=${CLI_REFRESH_VALUE}")
fi
"${DOCKER[@]}" build "${BUILD_ARGS[@]}" --file "$DOCKERFILE" --tag "$MUTABLE_TAG" "$CONTEXT"

# Each rebuild retains two full images, so without a bound the retained sets fill
# the disk. Keep the newest sets; a failure here leaves the build in place and
# says what it could not remove.
prune_retention_sets() {
    local listed
    if ! listed="$("${DOCKER[@]}" image ls --format '{{.Tag}}' "$MUTABLE_REPOSITORY" 2>/dev/null)"; then
        echo "WARN: retention prune skipped: image list failed" >&2
        return 0
    fi
    local keys
    keys="$(printf '%s\n' "$listed" \
        | sed -n -E 's/^armada-retained-(running|tag)-([0-9]{8}T[0-9]{6}Z-[0-9a-f]{16})$/\2/p' \
        | sort -r -u)"
    local index=0
    local removed=0
    local failed=0
    local key
    local kind
    for key in $keys; do
        index=$((index + 1))
        [ "$index" -gt "$RETAINED_KEEP" ] || continue
        for kind in running tag; do
            if printf '%s\n' "$listed" | grep -Fx "armada-retained-${kind}-${key}" >/dev/null; then
                if "${DOCKER[@]}" image rm "${MUTABLE_REPOSITORY}:armada-retained-${kind}-${key}" >/dev/null 2>&1; then
                    removed=$((removed + 1))
                else
                    failed=$((failed + 1))
                fi
            fi
        done
    done
    echo "retention_prune kept_sets=${RETAINED_KEEP} removed_tags=${removed} failed_tags=${failed}"
    [ "$failed" -eq 0 ] || echo "WARN: ${failed} retention tags could not be removed" >&2
    return 0
}

prune_retention_sets
