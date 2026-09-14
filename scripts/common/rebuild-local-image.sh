#!/usr/bin/env bash
# Rebuild one local Docker image only after immutable rollback tags exist.
set -euo pipefail

usage() {
    cat >&2 <<'USAGE'
Usage: rebuild-local-image.sh <running-container> <mutable-image-tag> <dockerfile> <context>

The helper records and retains the image used by the running container and the
current mutable tag before it runs a local `docker build`. Set
ARMADA_DOCKER_BIN to inject a Docker-compatible test double.
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

CONTAINER="$1"
MUTABLE_TAG="$2"
DOCKERFILE="$3"
CONTEXT="$4"
DOCKER_BIN="${ARMADA_DOCKER_BIN:-docker}"

is_safe_scalar "$CONTAINER" || die "running container name is invalid"
is_safe_scalar "$MUTABLE_TAG" || die "mutable image tag is invalid"
case "$MUTABLE_TAG" in
    */*:*) ;;
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

CONTAINER_INSPECTION="$("${DOCKER[@]}" container inspect --format '{{.State.Running}}\t{{.Image}}' "$CONTAINER" 2>/dev/null)" \
    || die "running container inspect failed"
CONTAINER_RUNNING="${CONTAINER_INSPECTION%%$'\t'*}"
RUNNING_IMAGE_ID="${CONTAINER_INSPECTION#*$'\t'}"
[ "$CONTAINER_RUNNING" = "true" ] || die "container is not running"
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

if "${DOCKER[@]}" image inspect "$RUNNING_RETENTION_TAG" >/dev/null 2>&1; then
    die "running retention tag already exists; refusing overwrite"
fi
if "${DOCKER[@]}" image inspect "$MUTABLE_RETENTION_TAG" >/dev/null 2>&1; then
    die "mutable retention tag already exists; refusing overwrite"
fi

# Keep both references before the build. If a later tag or the build fails,
# these references remain available for operator rollback.
"${DOCKER[@]}" image tag "$RUNNING_IMAGE_ID" "$RUNNING_RETENTION_TAG" \
    || die "running image retention tag failed"
"${DOCKER[@]}" image tag "$MUTABLE_IMAGE_ID" "$MUTABLE_RETENTION_TAG" \
    || die "mutable image retention tag failed"

echo "retained_running_image=${RUNNING_IMAGE_ID} tag=${RUNNING_RETENTION_TAG}"
echo "retained_mutable_image=${MUTABLE_IMAGE_ID} tag=${MUTABLE_RETENTION_TAG}"
echo "building_mutable_tag=${MUTABLE_TAG}"

# Fixed arguments and quoted arrays prevent image names or paths from becoming
# shell code. No push flag is accepted: this helper is for local rebuilds.
"${DOCKER[@]}" build --file "$DOCKERFILE" --tag "$MUTABLE_TAG" "$CONTEXT"
