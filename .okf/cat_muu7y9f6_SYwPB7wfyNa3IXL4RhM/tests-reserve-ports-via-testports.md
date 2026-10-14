---
type: "Project"
title: "Tests must reserve loopback ports through TestPorts.Reserve, never bind port 0 and release"
description: "Port 0 gives an OS ephemeral port that outbound sockets can take before the test binds it; use TestPorts.Reserve."
timestamp: "2026-10-05T11:44:00.4102630Z"
created: "2026-10-05T11:44:00.0813820Z"
slug: "tests-reserve-ports-via-testports"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
version: "1"
salience: "0.5"
---
Test servers and listeners get ports from Test.Shared/Infrastructure/TestPorts.Reserve(n), which draws randomly from 20000-31999 (below Linux/macOS/Windows ephemeral ranges; skips 21000-21099 and 25000-25099 reserved for manual local servers) and never hands a port out twice per process. Do not write helpers that bind TcpListener(IPAddress.Loopback, 0), read the port, Stop() it, and bind later: on 2026-10-05 CI macos net10 failed 3 relay tests with "Address already in use" because of exactly that (fixed in f6e88451 for RemoteDashboardRelayServiceSuite, ProxySecuritySuite, ProxyDashboardRelayIntegrationSuite, GitHubIntegrationSuite). Holding a port-0 listener open on purpose (e.g. ServerStartupSuite's port squatter) is fine.
