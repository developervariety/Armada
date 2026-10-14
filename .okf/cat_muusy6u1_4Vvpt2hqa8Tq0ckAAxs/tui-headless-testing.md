---
type: "Reference"
title: "Headless TUI testing with TuiTestHost and StubHttpHandler"
description: "TuiTestHost drives the real ArmadaTuiApp on a TUIKit HeadlessBackend with a StubHttpHandler (canned \"METHOD path\" responses, records requests, 404 otherwise) or a real server; feed keys by name and snapshot frames as text."
timestamp: "2026-10-05T05:40:10.7819380Z"
created: "2026-10-05T05:40:10.2918870Z"
slug: "tui-headless-testing"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-readme-frames-capture", "tui-parity-generator-and-suite", "test-assertions-structured-rule-7", "e2e-server-fixture"]
version: "1"
salience: "0.5"
---
src/Test.Shared/Infrastructure/TuiTestHost.cs: new TuiTestHost(width, height, StubHttpHandler? handler, serverUrl = "http://127.0.0.1:9", configure) builds HeadlessBackend + TerminalBackendAdapter + TuiApplication (never started, so hosts run sequentially) and the real ArmadaTuiApp composition root with a temp dir (armada-tui-test-<id>) holding tui.json and a FileCredentialStore; options Live=false, Utf8Probe true. Start() runs startup and pumps until done. Keys are fed as terminal bytes through TUIKit's parser by name (enter, tab, shift+tab, esc, arrows, home/end, pgup/pgdn, del, backspace, f1, f2, f5, f6, f10, f12, alt+left/right, shift+up/down, space) and snapshots render the composed frame including modals (TuiSnapshot). PumpUntil(condition, timeoutMs) waits for conditions. With handler null it uses real HTTP (Tui.EndToEnd against E2EServerFixture).
src/Test.Shared/Infrastructure/StubHttpHandler.cs: canned responses keyed by "METHOD path" (query ignored unless the key includes it), Map(method, path, json, status) or a function of the body, records "METHOD path?query" and bodies, 404 for anything unmapped. Fragility R5 plans a single request queue with route selection; assert on requests by route, not by position or body text. Fixtures: TuiFixtures, TuiEntityFixtures, TuiFlowRunner/TuiFlowSpec (table-driven keyboard flows), TuiPerfProbe, FakeModalHost, RecordingTerminal. Suites: Tui.* (Shell, Router, Grid, Widgets, Login, Ask, Approvals, KeyboardFlows, KeyboardFlows.Ops, Ops.*, Build.*, Activity.*, System.*, Display, Performance, Telemetry, Parity, ReadmeFrames, EndToEnd, EndToEnd.Flows).
