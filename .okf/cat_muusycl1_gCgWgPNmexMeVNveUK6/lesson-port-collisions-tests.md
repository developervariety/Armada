---
type: "Project"
title: "Test port collisions with the owner's server and parallel runs"
description: "Tests scanning fixed port ranges collided with a live server and with parallel agent runs; TestPorts picks random free ports and skips 21000-21099 and 25000-25099."
timestamp: "2026-10-05T05:25:55.5599890Z"
created: "2026-10-05T05:25:55.3347210Z"
slug: "lesson-port-collisions-tests"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["open-intermittent-refinement-test"]
version: "1"
salience: "0.5"
---
E2E.LoginSecurity and others failed with "Address already in use" when the owner's server (running on the test-written settings) held 21000/21001, and when several agents ran suites at once. Earlier, the E2E fixture picked ephemeral ports, released them, and rebound later (stolen 2-3 percent of the time). Fixes: E2E fixture reserves ports from 20000-31999 and retries startup; the Admiral fails startup if the MCP port is taken (it used to run silently without MCP); TestPorts (CI fixes) gives every test server random ports and avoids 21000-21099 and 25000-25099. When running many suites in parallel on one machine expect load-only intermittents (for example BacklogRefinementRoutes_CreateSendSummarizeApplyAndDelete) and rerun alone before concluding.
