---
type: "Reference"
title: "TUI telemetry (off by default; Telemetry section of tui.json)"
description: "Same fields as the Admiral (Enabled, ServiceName armada-tui, OtlpEndpoint, PrometheusEnabled false, PrometheusPort 9465, LokiEndpoint); exports Armada.Tui and TUIKit meters; armada_tui_* series with low-cardinality labels."
timestamp: "2026-10-05T05:40:01.7253680Z"
created: "2026-10-05T05:40:00.9952880Z"
slug: "tui-telemetry"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["telemetry-admiral", "tui-preferences-and-profiles", "lesson-mcp-host-binding"]
version: "1"
salience: "0.5"
---
Services/TuiTelemetry.cs (meter and activity source "Armada.Tui") and TuiTelemetrySettings. Off by default: no exporter, no port, and TUI and TUIKit instruments switched off (TuiTelemetry.Configure also sets TuiKitTelemetry.Enabled). Turn on in ~/.armada/tui.json "Telemetry": {"Enabled": true, "ServiceName": "armada-tui", "OtlpEndpoint": "http://127.0.0.1:4317", "PrometheusEnabled": false, "PrometheusPort": 9465, "LokiEndpoint": null}; read at startup. Helm's TuiCommand.StartTelemetryHost starts the Admiral's ArmadaTelemetryHost with AdditionalSources Armada.Tui and TUIKit (TuiStartOptions.TelemetryHostFactory). Series: armada_tui_sessions_total, armada_tui_screen_views_total{route,screen} (route pattern like /missions/:id, never an id), armada_tui_commands_total{command,source=key|palette|menu|direct,outcome=ok|error}, armada_tui_approval_decisions_total{kind,decision}, armada_tui_approval_latency_seconds{kind,decision}, armada_tui_ask_messages_total; spans armada.tui.command. Labels never carry ids, titles, message text, or URLs. Gotcha: the Prometheus listener binds localhost, which on macOS answers on ::1 only, so scrape localhost, not 127.0.0.1. Tests: Tui.Telemetry (MeterListener, ActivityListener, real scrape).
