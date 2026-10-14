---
type: "User"
title: "Prefer the owner's own libraries (Watson, Voltaic, SyslogLogging, PrettyId, TUIKit, Touchstone)"
description: "Watson (REST/WebSocket), Voltaic (MCP/JSON-RPC), SyslogLogging, PrettyId (IDs), TUIKit (TUI), Touchstone (tests) are the owner's libraries; use them rather than alternatives."
timestamp: "2026-10-05T05:23:24.9274820Z"
created: "2026-10-05T05:23:24.6921150Z"
slug: "pref-owner-libraries"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["open-tuikit-upstream-release"]
version: "1"
salience: "0.5"
---
CLAUDE.md says "Libraries (use these, they are mine)": Watson (REST API and WebSocket server), Voltaic (MCP / JSON-RPC server and client, McpHttpServer), SyslogLogging (logging; `private string _Header = "[ClassName] ";`), PrettyId (ID generation with prefixes such as msn_, vsl_). TUIKit (~/Code/Tuikit, NuGet TUIKit 1.2.1) powers the TUI and Touchstone runs the test suites; both are also the owner's. Gaps found in them are fixed upstream (TUIKit work happens on a branch in ~/Code/Tuikit, released by the owner) rather than worked around permanently or replaced.
