---
type: "Reference"
title: "AgentRuntimeFactory: creating runtimes, overrides, custom runtimes"
description: "AgentRuntimeFactory.Create(AgentRuntimeEnum) news up the runtime per launch; Override(type, factory) swaps a built-in (tests); Register(name, factory) + Create(name) serve Custom runtimes; ApiEndpoint needs an endpoint resolver."
timestamp: "2026-10-05T05:33:55.4840140Z"
created: "2026-10-05T05:33:55.1469240Z"
slug: "rt-runtime-factory"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-iagentruntime-interface", "rt-stub-runtimes-for-tests"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/AgentRuntimeFactory.cs. Constructed in ArmadaServer.StartAsync as new AgentRuntimeFactory(_Logging, ResolveInferenceEndpoint).

Create(AgentRuntimeEnum): ClaudeCode -> ClaudeCodeRuntime, Codex -> CodexRuntime, Gemini -> GeminiRuntime, Cursor -> CursorRuntime, Mux -> MuxRuntime, OpenCode -> OpenCodeRuntime, ApiEndpoint -> ApiAgentRuntime (throws InvalidOperationException if no model-endpoint resolver was configured), Custom -> throws (use Create(string name)). A new runtime instance is created per launch.
Override(type, Func<IAgentRuntime>) / ClearOverride(type): a ConcurrentDictionary consulted first by Create; this is how E2E tests install StubCaptainRuntime or stub processes without real CLIs ([[rt-stub-runtimes-for-tests]]). Register(name, factory) + Create(name): hook for code-registered Custom runtimes (not a product runtime; no planning or Ask). When the launch is delegated to a Harbor, RemoteHostProcessExecutor.CreateRuntime returns a RemoteAgentRuntime instead.
