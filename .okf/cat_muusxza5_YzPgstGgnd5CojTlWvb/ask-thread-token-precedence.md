---
type: "Reference"
title: "Ask thread token always wins; mixed identities are refused (F-31)"
description: "An Ask captain's per-thread session token takes precedence over any other credential on the MCP request; a thread token combined with a different identity, the API key, or an invalid bearer is refused."
timestamp: "2026-10-05T05:34:17.2078240Z"
created: "2026-10-05T05:34:16.9279600Z"
slug: "ask-thread-token-precedence"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["ask-captain-auto-approve-off", "security-identities", "tui-approvals-center"]
version: "1"
salience: "0.5"
---
Ask thread turns run a captain with a strict per-turn MCP config carrying a session token bound to that thread. That token is MCP-only (refused on REST and WebSocket) and turns state-changing tool calls into proposals the user must approve. F-31 (found by the usability workstream): a captain CLI that also sent the user's own bearer credential was authenticated by the bearer, bypassing the approval gate. Fix: when a thread token is present it always wins; a thread token alongside a credential for a different identity, the API key, or an invalid bearer gets the request refused. Test: Services.AskThreadTokenPrecedence. Related: Ask turns also run with auto-approve forced off (ask-captain-auto-approve-off), and approved proposals execute as the Ask caller through the MCP authorization wrapper.
