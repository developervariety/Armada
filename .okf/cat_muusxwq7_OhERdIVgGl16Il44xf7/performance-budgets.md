---
type: "Reference"
title: "REST performance budgets and baseline results (docs/PERFORMANCE.md)"
description: "p95 budgets: first page of any list 300 ms, deep page 500 ms, /status 300 ms, header activity poll 100 ms and under 10 KB, pageSize=9999 lists 500 ms; all met at 1x and 5x."
timestamp: "2026-10-05T05:32:50.5834590Z"
created: "2026-10-05T05:32:50.2741830Z"
slug: "performance-budgets"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["perf-baseline-script", "tui-frame-governor-performance"]
version: "1"
salience: "0.5"
---
Budgets (p95): first page of any list (missions, voyages, vessels, captains, vessel health, Ask threads, jobs) 300 ms; deep page 500 ms; GET /api/v1/status and dashboard summaries 300 ms; header activity poll (every 5-30 s per open dashboard) 100 ms and under 10 KB; full lists loaded with pageSize=9999 500 ms. Measured on an Apple M5 Max under load with SQLite, every endpoint is inside budget at 1x (500 vessels, 10k missions) and 5x; tightest at 5x: missions page 350 at 74 ms p95, status snapshot 50 ms. The one real fix: the header background-activity indicator downloaded every job ever run (861 KB) on each poll; it now calls GET /api/v1/jobs?status=Queued,Running (102 bytes, 1 ms). Full GET /api/v1/jobs is the heaviest at 5x (165 ms). Numbers are a regression baseline, not a benchmark. Reproduce with scripts/common/perf-baseline.sh.
