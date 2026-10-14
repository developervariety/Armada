---
type: "Reference"
title: "Mission: atomic work unit (msn_), statuses and key fields"
description: "Mission (msn_) = one task for one captain on one vessel; statuses Pending, Assigned, InProgress, WorkProduced, PullRequestOpen, Testing, Review, Complete, Failed, LandingFailed, Cancelled."
timestamp: "2026-10-05T05:29:24.0240470Z"
created: "2026-10-05T05:29:01.4396870Z"
slug: "term-mission"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-mission-state-machine", "term-mission-mode", "rt-mission-failure-kind", "term-voyage"]
version: "2"
salience: "0.5"
---
Term: Mission (src/Armada.Core/Models/Mission.cs, prefix msn_). One atomic unit of work assigned to one captain, targeting one vessel, optionally inside a Voyage.

MissionStatusEnum: Pending -> Assigned -> InProgress -> (Testing/Review) -> WorkProduced -> PullRequestOpen -> Complete; failure ends are Failed, LandingFailed (retryable back to WorkProduced), Cancelled. Terminal: Complete, Failed, Cancelled. Transition table: static class MissionStateMachine in src/Armada.Core/Services/MissionStateMachine.cs ([[arch-mission-state-machine]]).
Key fields: VoyageId, VesselId, CaptainId, RequestedCaptainId (preferred captain), Tier (fallback tier), AssignedHarborId, Title, Description, Mode (MissionModeEnum Implementation/Audit/Research), Priority (default 100, lower first), Persona, DependsOnMissionId (pipeline chain), ParentMissionId, BranchName, DockId, ProcessId, PrUrl, CommitHash, DiffSnapshot, AgentOutput, FailureReason, FailureKind (MissionFailureKindEnum, persisted since migration v77), WaitForVoyageWorkers (v77), RedispatchAttempts, review fields (RequiresReview, ReviewDenyAction, ReviewComment, ReviewedByUserId, ReviewDeadlineUtc...), SelectedPlaybooks/PlaybookSnapshots.
Branch name: armada/<sanitized captain name>/<missionId> (MissionService). Example: `armada mission show msn_abc123`.
