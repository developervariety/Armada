---
type: "Project"
title: "Tests never launch real agent CLIs; stub runtimes via AgentRuntimeFactory.Override"
description: "In-process test servers swap CLI runtimes for stubs (StubAgentProcesses, StubCaptainRuntime) through AgentRuntimeFactory.Override and ArmadaServer.RuntimeFactory; tool discovery uses a fake IRuntimeToolDiscoverySource."
timestamp: "2026-10-05T05:24:53.4657450Z"
created: "2026-10-05T05:24:53.0971280Z"
slug: "decision-test-runtime-stubs"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["lesson-ci-failures-2026-10-05"]
version: "1"
salience: "0.5"
---
Decided 2026-10-05 (CI fixes and TUI e2e work): tests must be host independent. AgentRuntimeFactory.Override(runtimeType, factory or null) plus ClearOverride, exposed via ArmadaServer.RuntimeFactory, let test servers replace every CLI runtime: StubAgentProcesses (CI fixes; processes the test owns) and StubCaptainRuntime (TUI e2e; calls MCP with the turn's session token, writes and commits a file for missions, scripted replies otherwise). Captain tool discovery goes through IRuntimeToolDiscoverySource (R3) so tests never run `claude mcp list`, read the owner's MCP configs, or probe his MCP servers. Result: CI no longer depends on the claude CLI (VoyageWorkflow_CreateAndCancel was failing on every OS) and the E2E run dropped from about 143 s to 84 s.
