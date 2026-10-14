---
type: "Reference"
title: "ApiEndpoint runtime: in-process tool-calling loop against a model endpoint"
description: "ApiAgentRuntime starts no process: it runs a tool-calling loop (default 100 iterations, clamp 1..1000) inside the Admiral against an Inference ModelEndpoint with built-in coding tools; reaches Armada MCP only when ARMADA_MCP_URL/ARMADA_MCP_TOKEN are supplied (Ask turns)."
timestamp: "2026-10-05T05:34:57.8124030Z"
created: "2026-10-05T05:34:57.4950920Z"
slug: "rt-api-endpoint-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["term-model-endpoint", "rt-ask-thread-mcp-token", "rt-iagentruntime-interface"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/ApiAgentRuntime.cs (AgentRuntimeEnum.ApiEndpoint). Resolves the captain's model endpoint through the factory's resolver (ArmadaServer.ResolveInferenceEndpoint); the endpoint's model is used unless the captain sets Model. Runs up to maxIterations (default 100, clamped 1..1000) tool-call rounds with built-in tools from src/Armada.Runtimes/Tools (BuiltInToolRegistry): read_file, write_file, edit_file, multi_edit, glob, grep, list_directory, manage_directory, file_metadata, delete_file, run_process, plan_tasks/update_task (TaskPlan), bounded by ToolSafetyLimits.

MCP: when the environment carries ARMADA_MCP_URL and ARMADA_MCP_TOKEN (Ask Armada turns do; token sent as X-Token), it connects with McpToolClient (Armada.Runtimes/Mcp) and exposes Armada's tools to the model as that user; missions do not supply them, so mission runs get coding tools only. Tool activity goes out on a typed OnToolEvent channel (ApiRuntimeToolEvent, phases), so reply text cannot fake tool cards; stdout carries only reply text. Synthetic PIDs. SupportsPlanningSessions=false; reasoning effort ignored; usable for Ask threads and missions. When delegated to a Harbor, the resolved endpoint travels as HarborInferenceEndpoint. Diagnostics: ApiRuntimeDiagnostic/Kind.
