---
type: "Feedback"
title: "CI: only a quick smoke test on push; nothing scheduled"
description: "Owner is tired of CI failures: push runs a ~1 minute smoke test; full, parity, install-verify, and security workflows are manual only; never add cron schedules."
timestamp: "2026-10-15T15:24:16.5557926Z"
created: "2026-10-15T15:24:16.2260602Z"
slug: "ci-smoke-only-no-schedules"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["ci-workflow-ci-yml"]
version: "1"
salience: "0.5"
---
On 2026-10-15 the owner said they are "so sick of CI failures". They asked that a push run only a simple, quick smoke test, and that nothing run on a regular basis.

Applied:
- ci.yml is the smoke test.
- full.yml, db-parity.yml (formerly nightly-parity), install-verify.yml and security.yml are workflow_dispatch only.
- release.yml stays on version tags.

How to apply:
- Never add a `schedule:` trigger.
- Never add slow or flaky suites to the smoke prefixes.
- Fix a flaky test at its race rather than retrying it. For example, Tui.HarborMetrics waited for the first Harbor-filtered request while two different requests raced.
- Run full.yml or the full local suite before releases.
