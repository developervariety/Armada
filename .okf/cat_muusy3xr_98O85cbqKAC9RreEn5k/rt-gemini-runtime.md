---
type: "Reference"
title: "Gemini runtime: gemini --approval-mode yolo"
description: "`gemini [--model m] --approval-mode yolo` with prompt on piped stdin; isolation writes .gemini/settings.json and redirects HOME/USERPROFILE; Ask turns use a throwaway .gemini/settings.json with --allowed-mcp-server-names armada."
timestamp: "2026-10-05T05:34:25.1121970Z"
created: "2026-10-05T05:34:24.8504390Z"
slug: "rt-gemini-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-launch-isolation", "rt-ask-thread-mcp-token"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/GeminiRuntime.cs (executable "gemini"). Command: gemini [--model <model>] --approval-mode yolo (auto-approve on); Gemini reads the prompt from piped stdin when no -p is given. Reasoning effort is ignored. Output is plain text read line by line (no tool cards; thinking only via the prompted <thinking> block).

Isolated launch (IsolateCaptainLaunch): writes .gemini/settings.json with mcpServers.armada { type http, url http://localhost:<mcpPort>/mcp } and points HOME and USERPROFILE at the scoped dir. Ask thread turns: .gemini/settings.json in the turn's throwaway working directory (httpUrl, header $ARMADA_MCP_TOKEN), GEMINI_CLI_TRUST_WORKSPACE=true (untrusted folders load no MCP servers), --allowed-mcp-server-names armada. Built-in tool inventory read by Armada.Server/RuntimeTools/GeminiBuiltInToolInventoryReader. Instructions file: GEMINI.md. Install: npm install -g @google/gemini-cli.
