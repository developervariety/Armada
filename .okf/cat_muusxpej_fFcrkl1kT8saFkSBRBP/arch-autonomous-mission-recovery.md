---
type: "Project"
title: "Autonomous mission recovery: incidents and bounded rescue missions"
description: "MissionRecoveryCoordinator (every tick) opens a recovery Incident for each recoverable mission that failed in the last 24h, dispatches up to MaxMissionRecoveryAttempts (2) rescue missions, and moves the incident Open -> Mitigated -> Closed from mission evidence; 0 disables."
timestamp: "2026-10-05T05:41:04.8668340Z"
created: "2026-10-05T05:41:04.5368460Z"
slug: "arch-autonomous-mission-recovery"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-incident", "rt-mission-failure-kind", "rt-stall-detection"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/MissionRecoveryCoordinator.cs, MaintainAsync called from the Admiral health loop; runs under a system admin AuthContext with no tenant/user so incident snapshots carry no owner FK.

Pass: (A) for each existing recovery incident not Closed/RolledBack: if Mitigated -> Closed ("Fix landed and held"); if any rescue mission is Complete -> Mitigated; if a rescue is still active (Pending..PullRequestOpen) wait; else if RecoveryAttempts < MaxMissionRecoveryAttempts dispatch another rescue (DispatchRescueAsync, includes a diff excerpt up to 4000 chars) and record it in RescueMissionIds; else note "Autonomous rescue exhausted the attempt budget; needs a human." (B) For Failed and LandingFailed missions updated within the last 24 hours that are not already linked to an incident and whose MissionFailureClassifier kind IsRecoverable (Compile, TestFail, LandingConflict, Timeout, Crash), open an incident and dispatch the first rescue. Idempotent because incidents link the missions they cover. Setting MaxMissionRecoveryAttempts (default 2; 0 disables). Distinct from captain stall recovery (MaxRecoveryAttempts 3, relaunch same mission).
