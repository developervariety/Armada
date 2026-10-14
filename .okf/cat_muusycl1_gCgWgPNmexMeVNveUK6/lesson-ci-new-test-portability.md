---
type: "Project"
title: "New tests must be portable: Windows file names, line endings, timing on shared runners"
description: "CI failures from new tests: '>' not allowed in Windows file names, platform newline expectations, absolute CPU thresholds and early frame captures on slow macOS runners."
timestamp: "2026-10-05T05:25:32.5930400Z"
created: "2026-10-05T05:25:32.3353980Z"
slug: "lesson-ci-new-test-portability"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
links: ["lesson-ci-failures-2026-10-05", "pref-root-cause-not-band-aid"]
version: "1"
salience: "0.5"
---
After the 2026-10-05 fixes, five new CI failures came from tests written the same day: Windows - tests created an untracked file "a -> b.txt" ('>' is invalid in Windows file names; use a different unusual name on Windows, keep the arrow case for POSIX), and a write_file test expected "\n" while the tool writes Environment.NewLine for new files (documented behavior); macOS - an idle CPU test asserted under 10 percent of a core (a shared runner measured 10.05; now compare governed vs ungoverned measured in the same run plus a deterministic redraw count) and a TUI mission-detail test captured the frame before the PR check row loaded (wait for the specific text). Rules: never use characters invalid on Windows in test file names; compute expected line endings with Environment.NewLine where the product uses it; avoid absolute performance thresholds; wait for the exact element you assert.
