---
type: "Reference"
title: "Runbook and runbook execution (playbook-backed, rbx_)"
description: "A Runbook is a playbook-backed operational procedure (its Id equals the backing playbook id) with parameters and steps; a RunbookExecution (rbx_) tracks one run with status Running/Completed/Cancelled."
timestamp: "2026-10-05T05:32:38.4961730Z"
created: "2026-10-05T05:32:38.1910390Z"
slug: "term-runbook"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-playbook", "term-incident", "arch-delivery-subsystem"]
version: "1"
salience: "0.5"
---
Terms (src/Armada.Core/Models/Runbook.cs, RunbookExecution.cs; RunbookService):
- Runbook: an executable operational runbook backed by a Playbook; Runbook.Id matches PlaybookId (pbk_). Carries RunbookParameter list and RunbookStep list (steps get rbs_ ids), optional links to environments/deployments/incidents.
- RunbookExecution (rbx_): event-backed record of one execution with parameter values, step completion tracking, status RunbookExecutionStatusEnum Running, Completed, Cancelled; linked deployment/incident.
Shown under Delivery > Runbooks. REST /api/v1/runbooks (runbook executions under it); MCP get_runbook, get_runbook_execution, start_runbook_execution; enumerate entityType runbooks / runbook_executions; WebSocket runbook-execution.changed. Category B config (TenantWide/UserSpecific).
