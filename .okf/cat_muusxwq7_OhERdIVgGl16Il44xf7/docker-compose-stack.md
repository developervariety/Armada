---
type: "Reference"
title: "Docker stack: docker/armada/compose.yaml and ARMADA_INITIAL_ADMIN_PASSWORD"
description: "cd docker/armada; export ARMADA_INITIAL_ADMIN_PASSWORD (8+ chars, required); docker compose up -d starts armada-server, armada-dashboard (3000), Prometheus, Loki, Grafana; config armada.json mounted as /app/data/settings.json."
timestamp: "2026-10-05T05:30:56.1977630Z"
created: "2026-10-05T05:30:55.4701210Z"
slug: "docker-compose-stack"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["docker-images-and-nonroot", "default-credentials-bind-guard", "initial-admin-password-env", "harbor-setup-split-mode", "telemetry-admiral"]
version: "1"
salience: "0.5"
---
docker/armada/compose.yaml: armada-server (ports 7890, 7891, 9464; ARMADA_DATA_DIR=/app/data; ./armada.json mounted at /app/data/settings.json; db/ and logs/ bind mounts), armada-dashboard (nginx-unprivileged, host 3000 -> container 8080, ARMADA_SERVER_URL=http://armada-server:7890, serves /dashboard/ and proxies API, /ws, images, i18n), prometheus 9090, loki 3100, grafana 3001 (admin/admin; dashboards "Armada Reliability" and "Armada Fleet Operations"). compose.split.yaml is the split-mode Admiral (with armada.split.json). The container binds 0.0.0.0, so the Admiral refuses to start while default credentials exist: ARMADA_INITIAL_ADMIN_PASSWORD (8+ characters; via env or docker/armada/.env) is required by compose (${VAR:?...}) and replaces the default password on first start, retiring the "default" bearer token. Sign in as admin@armada with that password; for scripts create a credential (shown once) and use Authorization: Bearer. MCP from the host needs a credential header (not loopback-bound in the container).

The server image contains git but no agent CLIs: in Local mode it can only run ApiEndpoint captains; CLI captains need split mode with a Harbor. docker/update.sh (or .bat) pulls the latest images and recreates the stack without touching volumes. Factory reset: docker/armada/factory/reset.sh|bat (prompts, stops, deletes SQLite db and logs, keeps armada.json). Logs: docker compose logs -f armada-server.
