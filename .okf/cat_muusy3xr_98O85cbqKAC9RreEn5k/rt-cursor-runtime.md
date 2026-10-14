---
type: "Reference"
title: "Cursor runtime: cursor-agent -p --force"
description: "`cursor-agent -p [--model m] --force --output-format text`; isolation writes .cursor/mcp.json and redirects HOME/USERPROFILE; Cursor MCP config is project-scoped otherwise."
timestamp: "2026-10-05T05:34:30.8881570Z"
created: "2026-10-05T05:34:30.5435470Z"
slug: "rt-cursor-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-launch-isolation", "rt-ask-thread-mcp-token"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/CursorRuntime.cs (executable "cursor-agent"). Command: cursor-agent -p [--model <model>] --force --output-format text; --force is the auto-approve switch; prompt on stdin. Reasoning effort ignored; plain-text output (no tool cards).

Isolated launches write .cursor/mcp.json (mcpServers.armada http url) and override HOME and USERPROFILE like Gemini. Outside isolation Cursor's MCP configuration is project-scoped (.cursor/mcp.json in the working directory), which is why the captain tool inventory often cannot see it. Ask thread turns: .cursor/mcp.json in the throwaway directory with header ${env:ARMADA_MCP_TOKEN}; each armada alias in ~/.cursor/mcp.json is redefined as the scoped server; --force approves it. Instructions file: CURSOR.md. Install: see docs.cursor.com/cli. CLI `armada captain add --runtime cursor`.
