---
type: "Project"
title: "Test data isolation and host independence rules"
description: "Suites create and clean their own data, never assert global counts, never depend on installed CLIs or host git config, and wait for conditions with deadlines instead of sleeping."
timestamp: "2026-10-05T05:27:12.7267590Z"
created: "2026-10-05T05:27:12.4629490Z"
slug: "test-data-isolation-rules"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["e2e-server-fixture", "flaky-test-handling", "test-assertions-structured-rule-7", "lesson-ci-new-test-portability"]
version: "1"
salience: "0.5"
---
From docs/TESTING.md and the infrastructure:
- Each suite creates its own entities, asserts only on them, tracks their ids, and deletes them at the end. Suites never assume an empty database, never assert exact totals across entity types, and can run in any order.
- Results must not depend on the machine: in-process servers use StubAgentProcesses (no real agent CLI), git is pinned by TestGitEnvironment, the profile is sandboxed by TestTemp, ports come from TestPorts.
- Wait for conditions, not durations: poll for the needed state with a deadline (MonotonicDeadline, JobWait, ReadinessProbe) or wait on a stub signal (for example FakeHealthCriterion.FirstEvaluationStarted). Use ManualClock where time matters.
- Timing assertions must survive loaded shared runners: compare against a baseline measured in the same run (Tui.Performance compares governed CPU to the ungoverned loop) rather than absolute thresholds.
- Every behavioral case carries TestTags.Positive or TestTags.Negative.
