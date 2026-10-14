---
type: "Reference"
title: "Ask Armada REST routes (/api/v1/ask/...)"
description: "Owner-scoped thread API: POST /ask/threads(/enumerate), GET/PUT/DELETE /ask/threads/{id}, POST .../messages (202 {MessageId, TurnId}), .../messages/enumerate (cursor), .../cancel, .../summarize, .../read, .../actions, .../proposals/{pid}/approve|reject, GET .../work/{workId}, GET /ask/quick-actions."
timestamp: "2026-10-05T05:49:44.9832110Z"
created: "2026-10-05T05:49:44.3929830Z"
slug: "api-ask-rest-routes"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["arch-ask-armada-flow", "api-mcp-ask-gate", "api-websocket-events", "decision-v1-not-ready-surfaces-d3"]
version: "1"
salience: "0.5"
---
src/Armada.Server/Routes/AskRoutes.cs; all Authenticated and owner-scoped (another user's thread -> 404); frozen in docs/API_SURFACE_1.0.md.
- POST /api/v1/ask/threads/enumerate {PageNumber, PageSize, Search, IncludeArchived} -> EnumerationResult<AskThread> (pinned first, then LastMessageUtc desc)
- POST /api/v1/ask/threads {Title?, CaptainId?, AutoApprove?} -> 201 AskThread
- GET /api/v1/ask/threads/{id} -> AskThreadDetail {Thread, TrackedWork[], PendingProposals[]}; PUT {Title?, CaptainId?, AutoApprove?, Pinned?, Archived?}; DELETE -> 204 (deletes messages, tool calls, proposals, tracked work; not the work)
- POST .../messages/enumerate {BeforeSequence?, PageSize} -> AskMessagePage {Messages (with ToolCalls, Proposal, TrackedWork), HasMore}
- POST .../messages {Content, ShowThinking?} -> 202 AskMessageSendResponse {MessageId, TurnId}; stream over WebSocket ask.*
- POST .../cancel (stop running turn), .../summarize (202; Summary message + SummaryText), .../read (UnreadCount = 0)
- POST .../actions {ToolName, Arguments} -> AskActionProposal (quick action, executed; same tool names/args as MCP)
- POST .../proposals/{pid}/approve | reject -> AskActionProposal
- GET .../work/{workId} -> AskWorkSnapshot; GET /api/v1/ask/quick-actions -> List<AskQuickAction> (dispatch, fleet-action, status, health, import)
Direct one-off captain chat: POST /api/v1/captains/{id}/chat. The old keyword POST /api/v1/ask and `armada ask` were removed (decision D3).
