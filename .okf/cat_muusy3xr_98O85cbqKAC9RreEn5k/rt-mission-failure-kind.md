---
type: "Reference"
title: "MissionFailureKindEnum: persisted mission failure classification"
description: "Mission.FailureKind (column since v77) is set where the failure happens: Compile, TestFail, Timeout, LandingConflict, Crash, NoOp, Boundary, ScopeViolation, JudgeRejected, Infra, Unknown, ReviewDenied, DependencyFailed, MaxRuntimeExceeded, StallRecoveryExhausted, OperatorAction, InvalidOutput."
timestamp: "2026-10-05T05:35:59.6948940Z"
created: "2026-10-05T05:35:59.2332630Z"
slug: "rt-mission-failure-kind"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-runtime-exit-info", "arch-autonomous-mission-recovery", "decision-structured-judge-and-failure-kind", "arch-mission-completion-pipeline"]
version: "1"
salience: "0.5"
---
Enum src/Armada.Core/Enums/MissionFailureKindEnum.cs; classifier src/Armada.Core/Services/MissionFailureClassifier.cs; DB column missions.failure_kind added by migration v77 on all providers (MissionFailureKindColumn helper). Set at the failure site, never inferred from FailureReason text.

Sources: Compile/TestFail/Timeout/Infra from the in-dock Definition-of-Done gate (FromDefinitionOfDone maps DefinitionOfDoneOutcomeEnum); LandingConflict for LandingFailed (also the classifier default for LandingFailed with no kind); Crash from a non-clean agent exit; NoOp for an exhausted no-op false-complete; Boundary for dock-boundary scan findings (secrets, protected paths, private identifiers); ScopeViolation for edits outside the mission's scope; JudgeRejected / InvalidOutput for Judge verdict failures or unsubstantiated verdicts; ReviewDenied for a human review deny with FailPipeline; DependencyFailed for downstream stages cancelled after an upstream failure; MaxRuntimeExceeded (MaxMissionRuntimeMinutes backstop); StallRecoveryExhausted; OperatorAction; Unknown.
IsRecoverable (drives autonomous rescue missions): Compile, TestFail, LandingConflict, Timeout, Crash. Distinct from the runtime-level RuntimeFailureKindEnum ([[rt-runtime-exit-info]]). Decision context: [[decision-structured-judge-and-failure-kind]].
