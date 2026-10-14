---
type: "Project"
title: "Intermittent: BacklogRefinementRoutes_CreateSendSummarizeApplyAndDelete returns 409 under load"
description: "E2E.Objective refinement route test fails \"expected OK got Conflict\" in full runs under heavy load, passes alone; likely captain still Refining when deleted; not yet root-caused."
timestamp: "2026-10-05T05:27:06.5632560Z"
created: "2026-10-05T05:27:05.9841100Z"
slug: "open-intermittent-refinement-test"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
links: ["lesson-port-collisions-tests"]
version: "1"
salience: "0.5"
---
Seen twice on 2026-10-05 in full local runs while many agents ran suites; passes 3/3 alone and has not appeared in CI. Analysis so far: send-message requires the session Active (it is Active before create returns), so the 409 likely comes from a later step - most likely DELETE /api/v1/captains/{id} returning 409 because the captain is still Working/Refining/Analyzing right after the session delete, or the captain PUT in SetCaptainRuntimeAsync. Next: make each assertion name its step, reproduce under load, and fix the release ordering in ObjectiveRefinementCoordinator (StopAsync/DeleteAsync) if the captain is released asynchronously.
