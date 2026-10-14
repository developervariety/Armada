---
type: "Project"
title: "Missing or invisible entities throw KeyNotFoundException; routes map by type"
description: "Services throw KeyNotFoundException (not InvalidOperationException \"not found\"); RouteErrorMapper maps 404/400/403/409 by exception type; no ex.Message checks."
timestamp: "2026-10-05T05:24:30.3356170Z"
created: "2026-10-05T05:24:30.0496580Z"
slug: "decision-typed-not-found"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["decision-mcp-typed-error-codes"]
version: "1"
salience: "0.5"
---
Implemented 2026-10-05 (FRAGILITY_REMEDIATION R2): about 114 service throw sites changed from InvalidOperationException("... not found") to KeyNotFoundException; InvalidOperationException stays for state conflicts; ArgumentException for bad input. src/Armada.Server/Routes/RouteErrorMapper.cs chooses the REST status by exception type (missing 404, bad input 400, forbidden 403, blocking state 400/409 per route) and replaced 13 routes that searched ex.Message for "not found". Behavior changes: a missing entity referenced in a request body is 404 (was 400); planning/refinement routes 404 for missing captain/vessel/dock; delete routes 409 for a blocking state. Exceptions kept: FleetCategorizationService/VesselImportService/AskThreadService ArgumentException cases (would be mislabeled BatchNotFound otherwise).
