---
type: "Feedback"
title: "Confirm before public or irreversible actions (Docker Hub, tags, deletes)"
description: "build-all pushes images to Docker Hub (jchristn77/armada-server, :latest); confirm version and CI state first; never touch the owner's live data without approval."
timestamp: "2026-10-05T05:23:15.2855940Z"
created: "2026-10-05T05:23:15.0344520Z"
slug: "pref-confirm-outward-actions"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["pref-version-changes-need-approval", "lesson-test-overwrote-user-settings"]
version: "1"
salience: "0.5"
---
build-all.sh <tag> builds multi-arch images with docker buildx on the cloud builder and PUSHES jchristn77/armada-server and the proxy image as <tag> and latest to Docker Hub. Before running it, check that the code version matches the tag and CI is green, and confirm with the owner if anything is off (2026-10-05 he chose: bump to 1.0.0, wait for CI, then build). Also: never modify or delete the owner's ~/.armada data or restart his running server without asking; restoring his settings after a test overwrote them was done only after approval. A GitHub PAT he shares is used only as an environment variable for gh commands, never written to files or commits, and he should be reminded to revoke it.
