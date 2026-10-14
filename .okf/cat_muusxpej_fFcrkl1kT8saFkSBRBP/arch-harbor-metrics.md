---
type: "Project"
title: "Harbor metrics (phase 1, branch work/harbor-metrics, 2026-10-08)"
description: "GET /api/v1/harbors/{id}/metrics?range=1h|24h|7d built by HarborMetricsBuilder from harbor_jobs, harbor_link_samples, harbor_link_events, token_usage.harbor_id (migration 81); recorder hooks in HarborConnectionManager.Metrics."
timestamp: "2026-10-08T20:53:55.4057730Z"
created: "2026-10-08T20:53:51.9923320Z"
slug: "arch-harbor-metrics"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
version: "1"
salience: "0.5"
---
Admiral-owned per-Harbor charts. Recording: HarborMetricsRecorder (Core/Services) set as HarborConnectionManager.Metrics; writes harbor_jobs (row per launch: kind/runtime/model, launched/started/first output/ended, outcome Running/Succeeded/Failed/Stopped/Lost), link events (Connected on handshake, Reconnecting on close, Disconnected after harbor.heartbeatTimeoutSeconds grace via SweepAsync in the health loop; ReconcileOnStartupAsync before the listener starts), per-minute link samples from heartbeats. Protocol 1.0 additive: heartbeat sequence/lastRoundTripMs/reconnectCount/lastReconnectUtc and heartbeatAck (sent only for sequenced heartbeats); Harbor app shares one HarborLinkStatistics across sessions. Serving: HarborMetricsService + pure HarborMetricsBuilder; shared bucketing in Armada.Core.Metrics (TimeBucketLayout, TimeBucketMath, ConcurrencyProfile, HarborMetricsRanges: 1h/1min, 24h/30min, 7d/3h). MCP get_harbor_metrics, ArmadaClient.GetHarborMetricsAsync, token usage harborId filter. Retention follows retention.jobRetentionDays. Dashboard HarborMetricsPanel, mobile HarborMetricsSection. Phase 2 (not done): Harbor app and TUI charts. Schema version now 81.
