---
type: "Reference"
title: "Ask Armada thread, message, proposal, tracked work (ath_, amg_, aap_, atw_)"
description: "AskThread (ath_) is a private per-user conversation with a captain; state-changing tool calls become AskActionProposals (aap_) needing approval unless AutoApprove; started work is tracked (atw_)."
timestamp: "2026-10-05T05:31:09.2579040Z"
created: "2026-10-05T05:31:08.2328770Z"
slug: "term-ask-thread"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-ask-armada-flow", "api-mcp-ask-gate"]
version: "1"
salience: "0.5"
---
Terms (migration v75, all providers; models in src/Armada.Core/Models):
- AskThread (ath_): Title, CaptainId, AutoApprove (default false), SummaryText, Pinned, Archived, LastMessageUtc, MessageCount, UnreadCount. Private to the owning user; another user's thread reads as 404.
- AskMessage (amg_): Sequence (monotonic per thread), Role (AskMessageRoleEnum User/Assistant/System), Kind (AskMessageKindEnum Text, ActionProposal, ActionResult, WorkUpdate, Summary, Error), ContentText, ThinkingText.
- AskMessageToolCall (atc_): tool name, arguments/result text, ok, elapsed ms.
- AskActionProposal (aap_): ToolName, ArgumentsText, SummaryText, Source (Captain/QuickAction), Status (AskProposalStatusEnum Pending, Approved, Rejected, Expired, Executed, Failed); pending proposals expire after Ask.ProposalExpiryMinutes (60).
- AskTrackedWork (atw_): EntityType (Voyage, Mission, FleetActionRun, Job, VesselImportBatch), EntityId, State (Active, Succeeded, Failed, Cancelled), snapshot hash.
Quick actions: /dispatch, /fleet-action, /status, /health, /import (AskQuickActionCatalog). Retention: Retention.AskThreadArchiveAfterDays 90, AskThreadDeleteAfterDays 0. See [[arch-ask-armada-flow]], [[api-mcp-ask-gate]].
