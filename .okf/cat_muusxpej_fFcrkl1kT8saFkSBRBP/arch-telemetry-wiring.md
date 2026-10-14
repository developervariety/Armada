---
type: "Project"
title: "Telemetry wiring: ArmadaMetrics meter and the Radiant-based host"
description: "Code emits through System.Diagnostics.Metrics (ArmadaMetrics, meter \"Armada\") and the Armada ActivitySource at zero cost; ArmadaTelemetryHost (only Radiant dependency) starts an OpenTelemetry pipeline when telemetry.enabled, exporting OTLP, Prometheus (:9464 /metrics), and/or Loki."
timestamp: "2026-10-05T05:43:44.1001870Z"
created: "2026-10-05T05:43:43.7881650Z"
slug: "arch-telemetry-wiring"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-admiral-startup-sequence"]
version: "1"
salience: "0.5"
---
Files: src/Armada.Core/ArmadaMetrics.cs (static instruments on Meter "Armada": CaptainStalls, CaptainRecoveries, MissionsFailed, MissionRuntimeExceeded, ReviewsOverdue, HandoffsRedriven, DocksProvisioned, DocksReclaimed, merge-queue processed, fleet action target counters/histograms, health evaluations and criterion durations, vessel import items; exported as armada_*_total), src/Armada.Server/ArmadaTelemetryHost.cs (started first in StartAsync; RadiantHost with RadiantSettings(serviceName); observes the Armada meter plus web server and HTTP client instrumentation; forwards the SyslogLogging stream into a Radiant ILogger when log export is on), src/Armada.Core/Settings/TelemetrySettings.cs (enabled false, serviceName armada, otlpEndpoint, prometheusEnabled true, prometheusPort 9464, lokiEndpoint).

Design rules: core libraries take no telemetry-framework dependency (BCL only); label cardinality stays low (no vessel ids, paths, or commands in metric labels; ids only as span attributes). Spans: vessel_health.job / vessel_health.evaluate_vessel / stage:<Criterion>. The TUI has its own separate telemetry config (~/.armada/tui.json). Operating the Prometheus/Loki/Grafana stack is in the operations category (docs/TELEMETRY.md).
