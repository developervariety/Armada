---
type: "Reference"
title: "Thread-scoped MCP token for Ask turns (CaptainThreadMcpPlanner)"
description: "Each Ask Armada turn mints a session token bound to the thread (lifetime Ask.TurnTimeoutMinutes + 5) and wires it into the runtime's per-invocation MCP override via ARMADA_MCP_TOKEN / X-Token, so the server can gate tool calls as proposals."
timestamp: "2026-10-05T05:35:31.1762950Z"
created: "2026-10-05T05:35:30.6809000Z"
slug: "rt-ask-thread-mcp-token"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["api-mcp-ask-gate", "arch-ask-armada-flow", "rt-launch-isolation"]
version: "1"
salience: "0.5"
---
Planner: src/Armada.Core/Services/CaptainThreadMcpPlanner.cs (CaptainThreadMcpPlanRequest); token minted by SessionTokenService with an AskThreadId claim in CaptainChatService/AskTurnCoordinator. The token travels in env ARMADA_MCP_TOKEN and is sent as X-Token; where the CLI expands env vars in MCP config the token is never written to disk or argv. The CLI's own login stays put (no HOME/CODEX_HOME redirect).

Per runtime: Claude Code --strict-mcp-config --mcp-config <per-launch file> (token in file, deleted on exit; other armada entries ignored); Codex -c mcp_servers.<armada|armada_ask>={url, env_http_headers={"X-Token"="ARMADA_MCP_TOKEN"}, default_tools_approval_mode="approve"} and -c mcp_servers.<alias>.enabled=false for user aliases (codex exec never prompts, so approval happens in the Ask thread); Gemini throwaway-dir .gemini/settings.json + GEMINI_CLI_TRUST_WORKSPACE=true + --allowed-mcp-server-names armada; Cursor throwaway .cursor/mcp.json with ${env:ARMADA_MCP_TOKEN}; Mux --mcp-config <file> --strict-mcp-config; OpenCode OPENCODE_CONFIG_CONTENT fresh remote entry; ApiEndpoint ARMADA_MCP_URL + ARMADA_MCP_TOKEN. An "alias" = server name containing armada, URL on the Admiral MCP port, or command running armada/Armada.Helm. Custom runtimes and servers with MCP off are not gated; GET /api/v1/captains/{id}/tools reports askApprovalGated. Thread tokens are refused on REST and /ws. Verified with real CLIs 2026-10-04 (docs/CAPTAINS.md).
