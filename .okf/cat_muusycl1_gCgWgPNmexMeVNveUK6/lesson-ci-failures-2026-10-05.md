---
type: "Project"
title: "Root causes of the recurring CI failures (fixed 2026-10-05)"
description: "Missing claude CLI on runners, categorization job/captain release race, Windows CRLF autocrlf, host Path APIs in unit generation, request-history timing, compose secret validation."
timestamp: "2026-10-05T05:25:26.5590040Z"
created: "2026-10-05T05:25:26.2096130Z"
slug: "lesson-ci-failures-2026-10-05"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["decision-test-runtime-stubs", "lesson-ci-new-test-portability"]
version: "1"
salience: "0.5"
---
Recurring GitHub Actions failures (ci.yml matrix ubuntu/macos/windows x net8/net10) and their root causes: (1) VoyageWorkflow_CreateAndCancel expected InProgress got Open on every OS - a voyage becomes InProgress only when an agent launches; runners have no claude CLI so launch rolled back (passed on the dev Mac because Claude Code is installed) -> stub runtimes in test servers. (2) Fleet categorization "captain released expected Idle got Analyzing" / "already running" - the worker marked the job finished before releasing the captain, and its heartbeat rewrote the whole job row -> release captain and stop heartbeat before writing the finished state (same fix for vessel discovery). (3) Windows GitService merge tests - core.autocrlf=true on runners -> tests force core.autocrlf=false/core.eol=lf via GIT_CONFIG_COUNT. (4) Windows systemd/launchd unit tests - System.IO.Path on Windows produced \opt\My Apps -> TargetPath helper uses target-platform rules. (5) Seeded secrets test - request history written after the response -> wait for all records. (6) compose job - compose.yaml requires ARMADA_INITIAL_ADMIN_PASSWORD -> placeholder secrets only in the validation step. Lesson: reproduce Windows-only conditions on macOS (autocrlf=true global config, Windows-style path API) to verify fixes locally.
