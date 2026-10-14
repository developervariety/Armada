---
type: "Project"
title: "CaptainChatService and captain tool discovery"
description: "CaptainChatService runs a headless chat turn with any runtime in a throwaway dir (direct /captains/{id}/chat and Ask turns); CaptainToolService + RuntimeTools discover what tools a captain can see (built-ins, MCP servers from its config) and report askApprovalGated."
timestamp: "2026-10-05T05:44:32.3464900Z"
created: "2026-10-05T05:44:31.8789840Z"
slug: "arch-captain-chat-and-tool-discovery"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-ask-armada-flow", "rt-ask-thread-mcp-token", "rt-cursor-runtime"]
version: "1"
salience: "0.5"
---
CaptainChatService (src/Armada.Server/CaptainChatService.cs): launches the captain's IAgentRuntime headlessly in a throwaway working directory (no worktree), reads the final answer from the runtime's final-message artifact, streams ask.chunk / ask.thinking / ask.tool to the caller's own sockets (direct chat POST /api/v1/captains/{id}/chat with TurnId) or via callbacks (Ask: RunTurnAsync with a full prompt and thread-scoped token). Claude Code switches to stream-json for token deltas; thinking via native channels (Mux, OpenCode) or a prompted <thinking> block; metrics via ChatTurnMetricsBuilder. For ApiEndpoint captains it mints a session token so the loop reaches Armada MCP as the caller. CaptainPromptRunner (ICaptainPromptRunner) runs one-shot prompts (fleet categorization).
Tool discovery: CaptainToolService + CaptainRuntimeToolCatalogService and src/Armada.Server/RuntimeTools (HostRuntimeToolDiscoverySource, ClaudeBuiltInToolInventoryReader, GeminiBuiltInToolInventoryReader, McpServerToolProbe (stdio framing / HTTP tools/list), RuntimeJsonMcpConfigFile) read the runtime's MCP config and built-in tool inventory, locally or on the captain's Harbor, and McpToolRegistrar.DescribeAll describes Armada's own catalog. Exposed as GET /api/v1/captains/{id}/tools and MCP get_captain_tools (CaptainToolAccessResult, includes askApprovalGated).
