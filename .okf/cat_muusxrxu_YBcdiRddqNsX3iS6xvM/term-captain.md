---
type: "Reference"
title: "Captain: a worker agent (cpt_) and its states"
description: "Captain (cpt_) = one configured agent worker with a Runtime (ClaudeCode, Codex, Gemini, Cursor, Mux, OpenCode, ApiEndpoint, Custom); states Idle/Working/Planning/Refining/Analyzing/Stalled/Stopping/Quarantined."
timestamp: "2026-10-05T05:28:41.3268630Z"
created: "2026-10-05T05:28:40.4910210Z"
slug: "term-captain"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-captain-tier", "rt-quarantine-and-crash-loop", "arch-captain-routing-persona-tier", "rt-iagentruntime-interface"]
version: "1"
salience: "0.5"
---
Term: Captain (model src/Armada.Core/Models/Captain.cs, prefix cpt_). A worker agent the Admiral dispatches missions to. Key fields: Runtime (AgentRuntimeEnum), Model, SystemInstructions, AllowedPersonas (JSON list; null = any role), PreferredPersona, Tier (CaptainTierEnum Economy=0/Standard=1/Premium=2), ReasoningEffort (Off/Minimal/Low/Medium/High), RuntimeOptionsJson (e.g. Mux options, auto-approve), State, CurrentMissionId, CurrentDockId, ProcessId, RecoveryAttempts, QuarantineUntilUtc/QuarantineReason, LastHeartbeatUtc (last agent OUTPUT), LastProcessAliveUtc (process liveness).

CaptainStateEnum: Idle (available), Working (running a mission), Planning (reserved by a planning session), Refining (reserved by a backlog refinement session), Analyzing (reserved by vessel-import fleet categorization), Stalled, Stopping, Quarantined (out of the pool until QuarantineUntilUtc; lifted by the health check).

One captain runs one mission at a time in one dock. Captains are auto-created on demand up to MaxCaptains (0 = auto, effectively 5) and MinIdleCaptains keeps a warm pool. Example: `armada captain add claude-2 --runtime claude`. See [[term-captain-tier]], [[rt-quarantine-and-crash-loop]], [[arch-captain-routing-persona-tier]].
