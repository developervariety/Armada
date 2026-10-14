---
type: "Project"
title: "Judge verdict, failure kind, and runtime failures are structured (R1b)"
description: "Only a standalone [ARMADA:VERDICT] line counts; MissionFailureKind is persisted (migration 77); captain quarantine comes from RuntimeExitInfo, not log words."
timestamp: "2026-10-05T05:24:36.8795400Z"
created: "2026-10-05T05:24:36.6114050Z"
slug: "decision-structured-judge-and-failure-kind"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["pref-structural-not-textual-logic"]
version: "1"
salience: "0.5"
---
Implemented 2026-10-05 (FRAGILITY_REMEDIATION R1b): judge verdicts count only from a standalone [ARMADA:VERDICT] line outside code blocks; missing or conflicting verdict lines mean no verdict (landing blocked); blocking verdicts need labeled File:/Scenario: lines. Missions persist FailureKind (MissionFailureKindEnum incl. ReviewDenied, DependencyFailed, MaxRuntimeExceeded, StallRecoveryExhausted, OperatorAction, InvalidOutput) and WaitForVoyageWorkers (migration 77 on all four providers); MissionFailureClassifier switches on FailureKind; FailureReason is human text only. Captain quarantine/stall uses RuntimeExitInfo from exit code plus structured provider errors (IAgentRuntime.OnProviderError, HTTP status for API captains, Claude stream-json error result) instead of substrings like "403"/"billing". Architects emit a fenced armada-plan JSON block (ArchitectPlanParser); [ARMADA:STATUS] can only toggle InProgress/Testing. Prompts changed accordingly; real captains must follow them.
