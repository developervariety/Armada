---
type: "Project"
title: "Pitfalls when merging parallel agent branches"
description: "Duplicate features (two Jobs screens, two HubScreen.Hints, two runtime overrides), cross-branch regressions (MySQL IF NOT EXISTS), interface members breaking test doubles, CRLF whole-file conflicts."
timestamp: "2026-10-05T05:25:39.7876610Z"
created: "2026-10-05T05:25:39.4807830Z"
slug: "lesson-parallel-agent-merge-pitfalls"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["pref-read-results-before-commit", "pref-control-scope-and-converge"]
version: "1"
salience: "0.5"
---
Seen 2026-10-04/05 while merging ~15 agent branches: (1) Duplicate implementations - two agents each built a Jobs screen (the registry is last-write-wins, so one silently replaced the other; kept the Operations one), both added HubScreen.Hints, and both added a runtime override API and ArmadaServer.RuntimeFactory; check for duplicates after every merge (build errors catch members, not registrations). (2) Cross-branch regressions - one branch removed the MySQL ADD COLUMN IF NOT EXISTS rewrite while another added migration 77 with that syntax; caught by MigrationHygiene and confirmed with the real-MySQL parity script. (3) A new IGitService member broke test doubles in other branches. (4) CRLF/LF churn produced whole-file conflicts; use an LF-normalized 3-way merge (git merge-file on CR-stripped base/ours/theirs, then restore the file's endings). (5) Additive doc conflicts (CHANGELOG, plan progress logs, parity.json) are resolved by keeping both sides; parity.json by taking the higher status per (kind, key). Always run the full suite after each merge and read the result before committing.
