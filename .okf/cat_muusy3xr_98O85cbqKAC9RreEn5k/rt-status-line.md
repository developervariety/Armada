---
type: "Reference"
title: "[ARMADA:STATUS]: agent may only toggle InProgress and Testing"
description: "An `[ARMADA:STATUS] <MissionStatus>` line on STDOUT is honored only for InProgress <-> Testing (MissionStateMachine.IsAgentReportableTransition); review, completion, failure, cancellation are never taken from agent output."
timestamp: "2026-10-05T05:36:46.3649210Z"
created: "2026-10-05T05:36:45.9884840Z"
slug: "rt-status-line"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["rt-armada-protocol-lines", "arch-mission-state-machine"]
version: "1"
salience: "0.5"
---
Code: AgentLifecycleHandler.HandleAgentStdout -> ApplyAgentReportedStatusAsync (src/Armada.Server/AgentLifecycleHandler.cs); MissionStateMachine.IsAgentReportableTransition.

The value is parsed with Enum.TryParse<MissionStatusEnum> (case-insensitive) by ProgressParser. Only stdout counts, because CLIs print tool and command output (e.g. a file the agent cat-ed) on stderr in text mode. Allowed: InProgress -> Testing and Testing -> InProgress (informational phase toggle); anything else is logged at debug and ignored. Mission completion is decided by the process exit and the completion pipeline; Review comes from pipeline RequiresReview; failure from exit classification and gates. The combined-output handler also records the line as an informational progress signal.
