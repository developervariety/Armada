---
type: "Reference"
title: "E2EServerFixture and SecurityTestServer: in-process Admirals for tests"
description: "E2EServerFixture lazily boots one shared in-process ArmadaServer (temp SQLite, random ports, readiness polling); SecurityTestServer is a disposable per-suite Admiral with custom settings."
timestamp: "2026-10-05T05:26:54.6165910Z"
created: "2026-10-05T05:26:53.2621810Z"
slug: "e2e-server-fixture"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["test-ports-random-range", "stub-agent-runtimes-in-tests", "test-data-isolation-rules"]
version: "1"
salience: "0.5"
---
src/Test.Shared/Infrastructure/E2EServerFixture.cs: starts one ArmadaServer on first access (thread-safe) and shares it across every E2E suite. Backed by a temp SQLite DB; exposes AuthClient (test API key), UnauthClient, McpClient, BaseUrl (http://127.0.0.1:<restPort>), ApiKey, RestPort, McpPort, SessionTokenEncryptionKey (to mint tokens), the Server (to inspect registered routes and MCP tools), and a fake runtime tool discovery source. Readiness is confirmed by polling the REST and MCP health endpoints, not by sleeping. Torn down on process exit.

src/Test.Shared/Infrastructure/SecurityTestServer.cs: a dedicated IDisposable in-process Admiral for suites that need their own configuration (non-loopback hostname, changed default password, MCP settings); exposes Server, StubAgents, Settings, BaseUrl, McpUrl (IPv4 loopback), ApiKey (global admin). InProcessArmadaServer is a third variant. All of them get ports from TestPorts and install StubAgentProcesses for every CLI runtime, so no real agent CLI ever starts. Suites own their data: create, assert only on what they created, delete at the end; never assume an empty DB or assert global counts.
