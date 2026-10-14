---
type: "Project"
title: "Production gaps found while making tests structural (R5, 2026-10-05)"
description: "Possible tenant-isolation gap in MCP enumerate missions (filters/tenant scope ignored with includeDescription); MCP auth refusals lack Forbidden code; restore bad ZIP 500; WebSocket command.error has no code."
timestamp: "2026-10-05T05:41:41.5889830Z"
created: "2026-10-05T05:41:41.1404850Z"
slug: "open-r5-production-todos"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["open-fragility-remediation", "open-security-followups"]
version: "1"
salience: "0.5"
---
Reported by the R5 test remediation (work/fx-tests, 2026-10-05), needing production changes: (1) POSSIBLE TENANT-ISOLATION GAP - MCP enumerate missions with includeDescription=true ignores the voyage, vessel, status and captain filters, and the default summary path calls EnumerateSummariesAsync with no tenant scope; verify and fix with priority (the test narrows by createdAfter with a TODO). (2) MCP authorization refusals (AuthorizeMcpTool) return plain text with isError but no Forbidden ErrorCode. (3) POST /api/v1/restore with an invalid ZIP returns 500 InternalError instead of 400. (4) Text is the only signal: WebSocket command.error frames (no error code), vessel import missing categorization captain, unknown fleet action template variable, mission.status_changed carries the new status only in the message. (5) Message-only errors needing typed exceptions/codes: MCP bind failure, GitService dirty checkout, planning "not idle", dock provisioning failures, categorization errors, tunnel handshake errors, captain model validation.
