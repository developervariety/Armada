---
type: "Project"
title: "Docker install check: git refused a bind-mounted repo owned by another UID"
description: "On GitHub runners the mounted origin repo (UID 1001) and container user (UID 1654) differ, so git clone fails exit 128; trust only that path via GIT_CONFIG safe.directory in the test override."
timestamp: "2026-10-05T05:25:49.9909450Z"
created: "2026-10-05T05:25:49.7273210Z"
slug: "lesson-docker-install-dubious-ownership"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
version: "1"
salience: "0.5"
---
install-verify.yml docker job failed with missions stuck Pending and GitService "git failed (exit 128): Cloning into bare repository" because git's dubious-ownership check rejects a repository owned by another user; Docker Desktop on macOS maps ownership so it passed locally. Fix (2026-10-05, scripts/common/install-verify/verify-docker.sh): the generated compose override sets GIT_CONFIG_COUNT=1, GIT_CONFIG_KEY_0=safe.directory, GIT_CONFIG_VALUE_0=/iv/origin.git for the armada-server container only. Real deployments that bind-mount repos owned by another UID will hit the same error and need safe.directory configured or matching ownership; consider documenting this in docs/DOCKER.md.
