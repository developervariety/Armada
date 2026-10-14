---
type: "Reference"
title: "TestPorts: random loopback ports 20000-31999 for in-process servers"
description: "TestPorts.Reserve(n) picks distinct free random ports in 20000-31999, skipping 21000-21099 and 25000-25099 (reserved for manual local servers); never fixed ports."
timestamp: "2026-10-05T05:26:46.8919250Z"
created: "2026-10-05T05:26:46.1679660Z"
slug: "test-ports-random-range"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["e2e-server-fixture", "test-temp-profile-sandbox", "install-verify-scripts"]
version: "1"
salience: "0.5"
---
src/Test.Shared/Infrastructure/TestPorts.cs: Reserve(count) draws random candidates from 20000 to 31999 (below the ephemeral ranges of Linux, macOS, and Windows so outbound sockets never take them), never hands a port out twice in one process, skips 21000-21099 and 25000-25099 (left for manually started local servers), test-binds each on IPAddress.Loopback while holding all of them, then releases and returns them. It gives up after 500 tries.

Why random: several test processes run at once (two frameworks, several worktrees); a sequential scan sends all to the same first free port and the loser either fails to bind or talks to another process's server. Root cause of earlier E2E startup flakes was OS ephemeral port reuse plus a silent MCP bind failure (V1_READINESS W4.2); the Admiral now fails startup when the MCP port is taken. Other reserved ranges: install-verify uses 34000-34100 (IV_PORT_BASE), perf-baseline uses 25060/25061 by default. Never use 7890/7891 in tests.
