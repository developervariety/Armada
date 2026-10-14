---
type: "Reference"
title: "Docker images: build scripts, Docker Hub tags, non-root UIDs"
description: "build-all.sh <tag> builds multi-arch jchristn77/armada-server and armada-proxy on the cloud builder and pushes to Docker Hub (needs owner approval); images run as UID 1654 (server, proxy) and 101 (dashboard)."
timestamp: "2026-10-05T05:31:03.5532340Z"
created: "2026-10-05T05:31:03.1287470Z"
slug: "docker-images-and-nonroot"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["docker-compose-stack", "pref-confirm-outward-actions", "open-o15-docker-uid-upgrade"]
version: "1"
salience: "0.5"
---
Repo-root build-all.sh/.bat <tag> calls build-admiral (jchristn77/armada-server from src/Armada.Server/Dockerfile) then build-proxy (jchristn77/armada-proxy from src/Armada.Proxy/Dockerfile) with docker buildx on the cloud builder cloud-jchristn77-jchristn77 for linux/amd64 and linux/arm64/v8, tags <tag> and latest, --push, then pulls both back. scripts/<os>/build-server|build-dashboard|build-proxy|build-all build the same images (dashboard: jchristn77/armada-dashboard from src/Armada.Dashboard/Dockerfile). Pushing to Docker Hub is a public action: confirm with the owner first, and only after CI is green. Local only: `docker build -f src/Armada.Server/Dockerfile -t armada-server:local .`.

Images are non-root with pinned patch base tags (F-21): Admiral and proxy run as UID 1654 (app); dashboard as UID 101 on port 8080 (nginx-unprivileged; node:22 alpine build stage). After upgrading an old install whose bind mounts were created by root: `sudo chown -R 1654:1654 docker/armada/db docker/armada/logs docker/armada/armada.json` (security O-15). Fix ownership rather than making directories world-writable.
