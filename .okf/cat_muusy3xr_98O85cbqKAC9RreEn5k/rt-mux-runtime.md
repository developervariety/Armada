---
type: "Reference"
title: "Mux runtime: mux print with per-captain endpoint options"
description: "`mux print ... --output-format jsonl (--yolo | --approval-policy p) [--endpoint name] ... --working-directory dir <prompt>`; prompt is the positional last arg; needs a named endpoint validated by `mux probe --require-tools`."
timestamp: "2026-10-05T05:34:39.3675710Z"
created: "2026-10-05T05:34:38.6707810Z"
slug: "rt-mux-runtime"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-typed-stream-events", "rt-launch-isolation", "rt-reasoning-effort"]
version: "1"
salience: "0.5"
---
src/Armada.Runtimes/MuxRuntime.cs with MuxCommandBuilder, MuxCliService (executable "mux"). Command: mux print [--config-dir <dir>] [--mcp-config <dir>/mcp-servers.json] [--effort <level>] [--show-thinking] --output-format jsonl [--output-last-message <file>] (--yolo | --approval-policy <policy>) [--endpoint <name>] [--model <m>] [--base-url <url>] [--adapter-type <t>] [--temperature <t>] [--max-tokens <n>] [--system-prompt <path>] --working-directory <dir> <prompt>.

Per-captain options (MuxCaptainOptions in RuntimeOptionsJson): config dir, endpoint (required), base URL, adapter type, temperature, max tokens, system prompt path, approval policy (empty or "auto" -> --yolo; anything else -> --approval-policy; the only runtime whose approval policy is per-captain, MCP muxApprovalPolicy). --mcp-config is added when mcp-servers.json exists in --config-dir, MUX_CONFIG_DIR, or ~/.mux. Validation: `mux probe --require-tools`; endpoints browsable at /api/v1/runtimes/mux/endpoints. Native thinking (assistant_thinking) and tool cards (tool_call_proposed/completed) from MuxProtocolEvent JSONL. Isolation writes mcp-servers.json ({servers:[{name:"armada",transport:"http",url:"http://localhost:<port>",mcpPath:"/mcp"}]}) and sets MUX_CONFIG_DIR; Ask turns use --mcp-config <per-launch file> --strict-mcp-config (Mux 0.7.0+). Instructions file MUX.md. CLI: armada captain add mux-1 --runtime mux --mux-endpoint local-openai.
