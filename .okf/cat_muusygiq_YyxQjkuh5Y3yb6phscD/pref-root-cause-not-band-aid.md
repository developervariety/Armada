---
type: "Feedback"
title: "Fix CI and bugs at the root cause"
description: "Owner asks to \"address the root cause\" of CI failures; no retries, sleeps, or loosened assertions as fixes."
timestamp: "2026-10-05T05:23:09.6295600Z"
created: "2026-10-05T05:23:09.4615520Z"
slug: "pref-root-cause-not-band-aid"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["lesson-ci-failures-2026-10-05", "pref-structural-not-textual-logic"]
version: "1"
salience: "0.5"
---
For CI failures and bugs the owner explicitly asks for the root cause to be addressed (2026-10-05). Diagnose why (missing agent CLI on runners, CRLF, host path APIs, races, host-header binding), fix it in product or test infrastructure, and add a regression test that fails on the old code. Do not paper over with retries, longer timeouts, sleeps, or weakened assertions. Timing-sensitive tests should assert deterministic properties (e.g. redraw counts) or relative measurements taken in the same run rather than absolute thresholds.
