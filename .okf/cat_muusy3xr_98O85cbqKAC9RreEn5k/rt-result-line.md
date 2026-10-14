---
type: "Reference"
title: "[ARMADA:RESULT] COMPLETE: completion marker for read-only missions"
description: "Audit and Research missions are told to end with a standalone `[ARMADA:RESULT] COMPLETE` line followed by a brief summary; completion itself is still decided by the process exit."
timestamp: "2026-10-05T05:37:49.6689100Z"
created: "2026-10-05T05:37:49.4652380Z"
slug: "rt-result-line"
category: "cat_muusy3xr_98O85cbqKAC9RreEn5k"
links: ["term-mission-mode", "rt-armada-protocol-lines"]
version: "1"
salience: "0.5"
---
Contract text from src/Armada.Core/Services/MissionModeContract.cs (BuildReadOnlySection): for read-only modes (Audit, Research) the captain must not modify the repository, must deliver a written report, and must "End with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary." ProgressParser recognizes type "result"; result markers are re-detected from the mission log at exit. The marker documents intent but does not by itself complete a mission: a clean process exit drives HandleCompletionAsync, where read-only missions skip no-op rejection and landing. Architect missions must NOT emit [ARMADA:RESULT]. See [[term-mission-mode]].
