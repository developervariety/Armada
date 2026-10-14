---
type: "Project"
title: "Ask Armada internals: thread service, turn coordinator, approval gate, work tracker"
description: "AskThreadService (owner-scoped storage) + AskTurnCoordinator (one turn per thread via CaptainChatService with a thread-scoped MCP token) + AskActionService (wraps every MCP tool: read-only allowlist runs, others become proposals) + AskWorkTracker (snapshots, milestones, ask.work) wired in ArmadaServer."
timestamp: "2026-10-05T05:42:13.0498690Z"
created: "2026-10-05T05:42:12.2181730Z"
slug: "arch-ask-armada-flow"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-ask-thread", "api-mcp-ask-gate", "rt-ask-thread-mcp-token", "arch-websocket-hub"]
version: "1"
salience: "0.5"
---
Components (src/Armada.Core/Services/Ask, src/Armada.Server/Ask, src/Armada.Server/CaptainChatService.cs):
- AskThreadService: CRUD, message paging (sequence cursor), unread counts, auto titles, summaries, proposals, tracked work; every caller method resolves by (tenant, user, id); OnUserEvent -> ArmadaWebSocketHub.SendToUser (owner sockets only).
- AskTurnCoordinator: POST .../messages persists the user message, returns 202 {MessageId, TurnId}, runs the turn in background; one running turn per thread; prompt history built server-side from the last Ask.HistoryTurns (20) messages plus the thread summary; mints a thread-scoped session token (AskThreadId claim, lifetime TurnTimeoutMinutes 15 + 5); runs CaptainChatService.RunTurnAsync (runtime in a throwaway directory, final message from the final-message artifact); streams ask.chunk/ask.thinking/ask.tool/ask.turn; persists reply + tool calls + token usage; cancel; summarize; follow-up turn after an approval (OnProposalApproved -> StartFollowUpAsync); milestone narration (NarrateAsync, NarrationTimeoutSeconds 60).
- AskActionService: MCP tools are registered through WrapTool; a call whose RpcCallContext carries askThreadId is gated by AskToolPolicy (explicit read-only allowlist; anything else is state-changing). AutoApprove off -> Pending proposal + ActionProposal message + ask.proposal; tool returns "Proposed as aap_... waiting for the user's approval...". Approve executes the stored call in-process through the same handler under the approving user's ambient context. Quick actions (/ask/threads/{id}/actions) are created already approved (source QuickAction). Expiry after ProposalExpiryMinutes (60).
- AskWorkLinker maps results to tracked work (dispatch->Voyage, create/restart_mission->Mission, run_fleet_action->FleetActionRun, evaluate_vessel_health->Job, import/discover_vessels->VesselImportBatch(+Job), cancel_* refresh).
- AskWorkTracker: reacts to hub EntityChanged and sweeps every TrackerIntervalSeconds (5); AskWorkSnapshotBuilder builds the card, hash compare, ask.work push, AskMilestoneDetector posts WorkUpdate messages (captain-worded when idle, else deterministic; no repeats after restart); also expires proposals.
Design doc: docs/ASK_ARMADA_HOME_BASE.md (implemented 2026-10-04).
