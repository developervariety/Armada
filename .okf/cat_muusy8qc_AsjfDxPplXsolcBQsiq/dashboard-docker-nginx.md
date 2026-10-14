---
type: "Reference"
title: "Standalone dashboard container (nginx-unprivileged on 8080)"
description: "src/Armada.Dashboard/Dockerfile builds with node:22 alpine (npm ci, npm run build) and serves dist at /dashboard/ from nginx-unprivileged (UID 101, port 8080); nginx.conf template proxies API, /ws, images, i18n to ARMADA_SERVER_URL."
timestamp: "2026-10-05T05:42:00.0584890Z"
created: "2026-10-05T05:41:59.6314880Z"
slug: "dashboard-docker-nginx"
category: "cat_muusy8qc_AsjfDxPplXsolcBQsiq"
links: ["docker-compose-stack", "docker-images-and-nonroot", "dashboard-served-by-admiral"]
version: "1"
salience: "0.5"
---
Dockerfile: build stage node:22.x-alpine (pinned patch), copies package.json and package-lock.json, npm ci, copies the dashboard source, npm run build; runtime nginxinc/nginx-unprivileged (pinned), copies /app/dist to /usr/share/nginx/html/dashboard, installs nginx.conf as /etc/nginx/templates/default.conf.template (rendered at start with ARMADA_SERVER_URL, default http://armada-server:7890), EXPOSE 8080, runs as UID 101. In docker/armada/compose.yaml it is armada-dashboard on host port 3000; the root redirects to /dashboard/ and everything else (REST, /ws, images, translations) is proxied to the Admiral, so the browser only talks to port 3000. The Admiral serves the same build at :7890/dashboard. Image name jchristn77/armada-dashboard (scripts/<os>/build-dashboard.sh). install-verify checks both the Admiral /dashboard and the nginx one. Base images are bumped deliberately (security.yml).
