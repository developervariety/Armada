---
type: "Project"
title: "Pre-land gates: no-op detector, dock-boundary scan, Definition-of-Done, auto-land predicate"
description: "Before landing: NoOpCompletionDetector (fast+empty+trivial), DockBoundaryScanner (secrets, protected paths, private identifiers), DefinitionOfDoneGate (vessel build+test in the dock, host-serialized), AutoLandPredicate (max files/lines, allow/deny globs) decide fail, hold for review, or land."
timestamp: "2026-10-05T05:40:57.1311800Z"
created: "2026-10-05T05:40:56.5397570Z"
slug: "arch-definition-of-done-and-autoland"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-mission-completion-pipeline", "arch-review-gates", "rt-mission-failure-kind", "term-vessel"]
version: "1"
salience: "0.5"
---
All pure/injectable services in src/Armada.Core/Services, driven by MissionService.HandleCompletionCoreAsync:
- NoOpCompletionDetector: flags a runtime false-complete only when ALL hold: empty diff, fast exit, trivial/bare completion message (a real investigation that found nothing is not flagged). Response: re-dispatch to a fresh captain up to MaxNoOpRedispatchAttempts (1), then Failed with FailureKind NoOp and an inbox item.
- DockBoundaryScanner + DockBoundaryPolicy (vessel SecretScanEnabled, ProtectedPathPatterns, PrivateIdentifierDenylist): scans the diff and changed paths; findings carry only rule id, path, and line number (never secret bytes); any finding fails the mission with FailureKind Boundary and a redaction-safe reason. (Security details: security category.)
- DefinitionOfDoneGate (vessel DefinitionOfDoneEnabled, DefinitionOfDoneBuildCommand, DefinitionOfDoneTestCommand, timeout): runs build then unit tests inside the mission's own checkout, serialized host-wide (one at a time) so captains do not thrash CPU/shared build servers; classifies DefinitionOfDoneOutcomeEnum Pass/Compile/TestFail/Timeout/Infra; failure -> Failed with the matching FailureKind (recoverable for autonomous rescue).
- AutoLandPredicate + AutoLandPolicy (vessel AutoLandEnabled, AutoLandMaxFiles, AutoLandMaxLines, AutoLandPathAllowGlobs, AutoLandPathDenyGlobs; 0 = no limit): from the diff summary decides land vs hold; hold -> Review with "Auto-land held: ...". GET /api/v1/missions/{id}/evaluate-autoland and MCP evaluate_autoland preview the decision. Vessel RequirePassingChecksToLand ties landing to check runs.
