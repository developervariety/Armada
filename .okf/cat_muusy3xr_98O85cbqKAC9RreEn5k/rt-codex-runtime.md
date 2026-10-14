---
type: "Reference"
title: "Codex runtime: codex exec flags and quirks"
description: "`codex exec --skip-git-repo-check --sandbox workspace-write [--model m] [-c model_reasoning_effort=x] [--output-last-message file]`; Windows full-auto uses --dangerously-bypass-approvals-and-sandbox; isolation via CODEX_HOME config.toml."
timestamp: "2026-10-05T05:34:18.9851390Z"
created: "2026-10-05T05:34:18.4266380Z"
slug: "rt-codex-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-ask-thread-mcp-token", "rt-typed-stream-events", "rt-launch-isolation"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/CodexRuntime.cs (executable "codex"). Command: codex exec --skip-git-repo-check --sandbox workspace-write [--model <model>] [-c model_reasoning_effort=<minimal|low|medium|high>] [--output-last-message <file>]; prompt on stdin.

Notes: approval mode default full-auto is sent as --sandbox workspace-write because Codex 0.159 removed the --full-auto alias from codex exec (which never prompts); on Windows full-auto is sent as --dangerously-bypass-approvals-and-sandbox, and a "dangerous" mode sends that on every OS. --skip-git-repo-check lets chat/planning turns run in their throwaway non-git directory; captain validation runs git init in its scratch directory first. The final reply is read from the --output-last-message file. Isolated launches write config.toml with [mcp_servers.armada] url = http://localhost:<mcpPort>/mcp and set CODEX_HOME to the scoped dir. Ask turns use -c mcp_servers.<name>={ url, env_http_headers = { "X-Token" = "ARMADA_MCP_TOKEN" }, default_tools_approval_mode = "approve" } and disable other armada aliases. Typed stream events: CodexStreamEvent (thread.started, turn.*, item.*, error). Output read line by line (no tool cards). Instructions file: CODEX.md. Install: npm install -g @openai/codex.
