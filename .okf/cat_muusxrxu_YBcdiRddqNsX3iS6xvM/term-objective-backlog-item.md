---
type: "Reference"
title: "Objective / Backlog item (obj_): scoped future work"
description: "Backlog item is persisted as Objective (obj_); has kind, priority, rank, BacklogState (Inbox..Dispatched), Status (Draft..Completed/Blocked/Cancelled) and links to sessions, voyages, releases, incidents."
timestamp: "2026-10-05T05:30:47.1054120Z"
created: "2026-10-05T05:30:46.6298810Z"
slug: "term-objective-backlog-item"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-refinement-session", "term-planning-session", "arch-backlog-refinement-sessions"]
version: "1"
salience: "0.5"
---
Term: Objective = Backlog item (src/Armada.Core/Models/Objective.cs, prefix obj_; ObjectiveService). "Backlog" is the user-facing name; the record and compat routes keep "objective".

Fields: title, description, kind (ObjectiveKindEnum: feature, bug, refactor, research, chore, initiative...), category, priority (ObjectivePriorityEnum, e.g. P1), rank (manual order; reorder routes), BacklogState (ObjectiveBacklogStateEnum: Inbox, Triaged, Refining, ReadyForPlanning, ReadyForDispatch, Dispatched), Status (ObjectiveStatusEnum: Draft=0, Scoped=1, Planned=2, InProgress=3, Released=4, Deployed=5, Completed=6, Blocked=7, Cancelled=8), effort (ObjectiveEffortEnum), owner, target version, due date, parent/blocked-by links, acceptance criteria, non-goals, rollout constraints, evidence links, linked fleets/vessels/sessions/voyages/missions/checks/releases/deployments/incidents. Normalized tables since migration 43 (v0.8.0); objective.snapshot events keep the audit trail.
Can be imported from a GitHub issue/PR (POST /api/v1/objectives/import/github). Lifecycle: refine ([[term-refinement-session]]) -> plan ([[term-planning-session]]) -> dispatch -> release/deploy/incident, all linked back. REST /api/v1/backlog (+ /objectives compat); MCP list_backlog, create_backlog_item, update_objective, reorder_backlog_items...; CLI `armada backlog ...`.
