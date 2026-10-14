---
type: "Project"
title: "O-14 (Low, open): Docker observability ports exposed, Grafana admin/admin, /metrics unauthenticated"
description: "The Docker stack publishes Prometheus 9090, Loki 3100, Grafana 3001 (admin/admin), and the Admiral's 9464 /metrics without auth. Owner: W5 / operations."
timestamp: "2026-10-05T05:37:00.7253510Z"
created: "2026-10-05T05:37:00.5230230Z"
slug: "open-o14-observability-ports"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["telemetry-admiral", "docker-compose-stack"]
version: "1"
salience: "0.5"
---
docker/armada/compose.yaml publishes the observability stack's ports on the host (Prometheus 9090, Loki 3100, Grafana 3001 with default login admin/admin) and the Admiral's Prometheus scrape endpoint on 9464 is unauthenticated whenever telemetry is enabled. On a networked host this exposes metrics and logs. Mitigations: firewall or remove the port mappings, change the Grafana password, bind to 127.0.0.1. Owner: W5 / operations. Status 2026-10-04: open.
