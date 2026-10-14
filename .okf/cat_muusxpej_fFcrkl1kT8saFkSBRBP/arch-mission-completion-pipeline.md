---
type: "Project"
title: "Mission completion pipeline (MissionService.HandleCompletionCoreAsync)"
description: "On clean exit: idempotency guard -> WorkProduced -> capture diff and agent output -> token usage -> no-op rejection -> dock-boundary scan -> Judge verdict -> pipeline handoff / review hold -> Definition-of-Done gate -> auto-land hold -> OnMissionComplete landing -> reclaim dock -> release captain."
timestamp: "2026-10-05T05:39:41.5637100Z"
created: "2026-10-05T05:39:41.0217030Z"
slug: "arch-mission-completion-pipeline"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-landing-handler", "arch-definition-of-done-and-autoland", "arch-review-gates", "arch-pipeline-stage-handoff", "rt-verdict-line", "rt-mission-failure-kind"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/MissionService.cs HandleCompletionAsync -> HandleCompletionCoreAsync (in-flight dedup guard), in order:
1. Idempotency: skip if already processed (concurrent health check / exit handler).
2. Read-only modes (Audit/Research) skip no-op rejection and landing.
3. Status -> WorkProduced; resolve dock (mission DockId, else captain's).
4. Capture diff BEFORE handoff (OnCaptureDiff -> MissionLandingHandler.HandleCaptureDiffAsync persists DiffSnapshot + CommitHash) and accumulated agent stdout (AgentOutput); record token usage (real, [ARMADA:TOKENS], or estimated).
5. NoOpCompletionDetector: fast exit + empty diff + trivial output -> re-dispatch to a fresh captain up to MaxNoOpRedispatchAttempts, then fail (NoOp) to the inbox. Scope validation can fail ScopeViolation.
6. DockBoundaryScanner (per-vessel DockBoundaryPolicy: SecretScanEnabled, ProtectedPathPatterns, PrivateIdentifierDenylist): finding blocks landing, FailureKind Boundary, redaction-safe reason (no secret bytes).
7. Judge missions: JudgeVerdictParser + JudgeContract; None/blocked -> fail (JudgeRejected/InvalidOutput).
8. Pipeline handoff to dependent stages (TryHandoffToNextStageAsync / Architect fan-out) or, if the stage RequiresReview, hold in Review (OnReviewRequested -> approval-needed).
9. In-dock Definition-of-Done gate (vessel build + unit tests, host-wide serialized): Compile/TestFail/Timeout/Infra fail the mission.
10. Auto-land predicate: too large or denied paths -> hold in Review ("Auto-land held: ...").
11. OnMissionComplete (landing, synchronous; captain stays Working until done), then reclaim dock, then release the captain only if still assigned to this mission; emit outcome telemetry; dispatch pending.
