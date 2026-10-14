---
type: "Reference"
title: "Admiral telemetry: OTLP, Prometheus :9464/metrics, Loki (off by default)"
description: "telemetry.enabled false by default (true in Docker); exports Armada meter as armada_*_total series plus runtime/HTTP metrics via the Radiant host; Armada sends nothing to its authors."
timestamp: "2026-10-05T05:32:04.5439700Z"
created: "2026-10-05T05:32:04.0450980Z"
slug: "telemetry-admiral"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["tui-telemetry", "docker-compose-stack", "open-o14-observability-ports"]
version: "1"
salience: "0.5"
---
docs/TELEMETRY.md. Code emits through System.Diagnostics.Metrics (no dependency in core libraries). When telemetry.enabled is true the Admiral hosts an OpenTelemetry pipeline (Radiant host, ArmadaTelemetryHost) that exports to otlpEndpoint (optional push), an in-process Prometheus endpoint (prometheusEnabled true, prometheusPort 9464, /metrics, unauthenticated; O-14), and lokiEndpoint (optional log push). serviceName default "armada". Docker's armada.json enables it pointed at the bundled loki; Prometheus scrapes armada-server:9464; Grafana (3001, admin/admin) is provisioned from docker/armada/observability/ (prometheus.yml, loki-config.yaml, grafana provisioning and dashboards).
Series: armada_captains_stalls_total, armada_captains_recoveries_total, armada_missions_failed_total, armada_missions_runtime_exceeded_total, armada_reviews_overdue_total, armada_handoffs_redriven_total, armada_docks_provisioned_total, armada_docks_reclaimed_total, armada_mergequeue_processed_total, armada_fleet_action_targets_total{kind,outcome}, armada_health_evaluations_total{outcome}, armada_vessel_import_items_total{outcome}; histograms armada_fleet_action_target_duration_seconds{kind}, armada_health_criterion_duration_seconds{criterion}; spans from the Armada activity source. Labels stay low-cardinality: never vessel ids, paths, or commands. The TUI has separate telemetry in tui.json.
