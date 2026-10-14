---
type: "Feedback"
title: "Read test results before committing; never push failing merges"
description: "A merge was once committed and pushed while its full test run showed 2 failures because the commit was chained after cat; check the totals first."
timestamp: "2026-10-05T05:23:19.5405900Z"
created: "2026-10-05T05:23:19.3415390Z"
slug: "pref-read-results-before-commit"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["pref-commit-and-push-to-main"]
version: "1"
salience: "0.5"
---
On 2026-10-05 a merge (work/tui-e2e) was committed and pushed in the same shell command that printed its full-suite results, which showed 2 failures. Always inspect the "Total: N Passed: N Failed: N" line (and the Failed Tests list) in a separate step before committing a merge or pushing. Intermittent failures must be identified as such (rerun the suite alone, check load) and recorded, not ignored.
