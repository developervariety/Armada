---
type: "Reference"
title: "Inbox (\"Needs You\"): items awaiting a human"
description: "InboxService builds a consolidated, most-urgent-first list of InboxItems: review, landing_failed, failed, stalled_captain, merge_failed, deployment_approval, deployment_failed, with severity Info/Warning/Critical."
timestamp: "2026-10-05T05:33:26.3829190Z"
created: "2026-10-05T05:33:23.6448690Z"
slug: "term-inbox-item"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-review-gates", "arch-mission-completion-pipeline"]
version: "1"
salience: "0.5"
---
Term: Inbox item (src/Armada.Core/Models/InboxItem.cs; src/Armada.Core/Services/InboxService.cs). The operator's "needs you" list: things awaiting a human decision or intervention, built on demand from current state (not stored), scoped to the caller, capped at 100 per category.

Kinds: review (mission waiting in Review), landing_failed, failed (mission), stalled_captain, merge_failed (merge entry), deployment_approval (PendingApproval deployment), deployment_failed. Each item carries EntityType/EntityId and InboxSeverityEnum (Info=0, Warning=1, Critical=2). Missions blocked by the dock-boundary scan, Definition-of-Done gate, or exhausted no-op redispatch surface here with redaction-safe reasons. Interfaces: REST GET /api/v1/inbox (plain array), MCP inbox, CLI `armada inbox [--critical]`, dashboard/TUI "Needs You".
