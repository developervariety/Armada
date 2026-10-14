---
type: "Project"
title: "Plan documents are living: update status markers and the dated progress log"
description: "V1_READINESS.md, TUI_APP_PLAN.md, FRAGILITY_REMEDIATION.md, SERVER_REBUILD.md use [ ] [~] [x] [!] markers, a one-line note per touched task, and a dated Progress Log row (Date | Who | Items | Notes) with deviations."
timestamp: "2026-10-05T05:44:28.8185980Z"
created: "2026-10-05T05:44:28.2525110Z"
slug: "living-plan-docs-update"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["security-review-living-document", "tui-plan-status-and-remaining-work", "docs-writing-style"]
version: "1"
salience: "0.5"
---
Plan docs are typed "implementation plan (work-tracking). Annotate task status and the progress log as you go; keep this document in sync with what actually shipped." Status values: [ ] not started, [~] in progress, [x] done, [!] blocked. When you touch a task: update its marker, add a one-line note under it, update the header Status/Last updated lines, and add a dated row to the Progress Log (absolute date, author such as "Claude (tui-build)", task ids, what changed, deviations from the plan, upstream gaps worked around). Files: V1_READINESS.md, TUI_APP_PLAN.md, FRAGILITY_REMEDIATION.md, docs/SERVER_REBUILD.md, docs/SECURITY_REVIEW.md (findings table). Commits often update them separately ("TUI_APP_PLAN.md: W0.4 done; ...", "FRAGILITY_REMEDIATION.md: R3 done").
