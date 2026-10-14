---
type: "Project"
title: "Use the owner's libraries: Watson, Voltaic, SyslogLogging, PrettyId (and TUIKit, Touchstone)"
description: "REST and WebSocket on Watson, MCP/JSON-RPC on Voltaic, logging on SyslogLogging, IDs on PrettyId; the TUI uses TUIKit and tests use Touchstone; do not introduce alternatives."
timestamp: "2026-10-05T05:43:49.0894510Z"
created: "2026-10-05T05:43:48.7572240Z"
slug: "owner-libraries-usage"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["pref-owner-libraries", "tuikit-upstream-status", "logging-header-pattern"]
version: "1"
salience: "0.5"
---
CLAUDE.md "Libraries (use these, they are mine)": Watson (NuGet) for the REST API and WebSocket (ArmadaServer uses _App.Routes.PreRouting), Voltaic (NuGet) for the MCP/JSON-RPC server, SyslogLogging for logging, PrettyId for prefixed IDs. The owner also maintains TUIKit (terminal UI; Armada.Tui) and Touchstone (test descriptors and runners: Touchstone.Core, Touchstone.Cli, xUnit/NUnit adapters). When a gap appears in one of these, prefer fixing it upstream (as with TUIKit workstream U) over adding a competing dependency. Telemetry uses Radiant. See also owner preference pref-owner-libraries.
