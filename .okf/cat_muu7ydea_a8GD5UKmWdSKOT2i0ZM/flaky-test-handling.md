---
type: "Project"
title: "Handling flaky or intermittently failing tests"
description: "A rerun that turns red to green is not a pass until the cause is known; root-cause flakes (ports, readiness, clocks, load) and fix them structurally; W4.2 target was 20 consecutive green runs."
timestamp: "2026-10-05T05:27:19.4449490Z"
created: "2026-10-05T05:27:19.1617800Z"
slug: "flaky-test-handling"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-data-isolation-rules", "test-ports-random-range", "pref-root-cause-not-band-aid", "lesson-ci-failures-2026-10-05"]
version: "1"
salience: "0.5"
---
Policy (docs/RELEASING.md step 2, owner preference for root causes): a rerun that turns a red job green is not a pass until you know why it was red; note any intermittent failure seen during a release in the release notes. V1_READINESS W4.2 (done 2026-10-04) root-caused E2E startup timeouts to OS ephemeral port reuse plus a silent MCP bind failure (fixed: random TestPorts range, readiness polling of REST and MCP, Admiral fails startup if the MCP port is taken), fixed 3 more flakes and a sleep-sensitive clock, and reached 20 consecutive green full runs. SQL Server timeouts were solved by running parity on native amd64 (ubuntu) instead of emulation on Apple Silicon.

When a test fails under load but passes alone, find the timing assumption (fixed sleeps, absolute CPU or latency bounds, capturing a frame before data loads) and replace it with a condition wait or a same-run baseline. Do not skip or delete the test, and do not loosen it into an always-pass check.
