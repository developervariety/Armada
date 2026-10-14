---
type: "Reference"
title: "Playbook: reusable markdown guidance attached at dispatch (pbk_)"
description: "Playbook (pbk_) = tenant-scoped markdown doc selected per voyage/mission with delivery InlineFullContent, InstructionWithReference, or AttachIntoWorktree; content snapshotted per mission."
timestamp: "2026-10-05T05:30:04.9784090Z"
created: "2026-10-05T05:30:04.6457870Z"
slug: "term-playbook"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-runbook", "arch-prompt-and-instructions-file"]
version: "1"
salience: "0.5"
---
Term: Playbook (src/Armada.Core/Models/Playbook.cs, prefix pbk_). Markdown instruction document (e.g. CSHARP_BACKEND_ARCHITECTURE.md) managed under Configuration > Playbooks, REST /api/v1/playbooks, MCP get/create/update/delete_playbook, CLI `armada playbook list|add|show|remove`.

At dispatch, SelectedPlaybook entries choose PlaybookDeliveryModeEnum: InlineFullContent=0 (markdown embedded in the instructions), InstructionWithReference=1 (instructions reference a materialized file), AttachIntoWorktree=2 (file placed in the worktree and hidden from git via the dock's exclude file). MissionService snapshots the exact content, filename, order, and resolved mode into Mission.PlaybookSnapshots (MissionPlaybookSnapshot) so later edits never change historical context. Ownership is Category B (TenantWide/UserSpecific scope). Runbooks are playbook-backed: a Runbook's Id is its playbook id ([[term-runbook]]). Example: `armada voyage create "Refactor" --vessel api --playbook CSHARP_BACKEND_ARCHITECTURE.md --mission "..."`.
