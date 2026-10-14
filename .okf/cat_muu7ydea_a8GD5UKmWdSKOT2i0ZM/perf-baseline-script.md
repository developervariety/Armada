---
type: "Reference"
title: "REST performance baseline: perf-baseline.sh with Armada.PerfSeed"
description: "scripts/common/perf-baseline.sh seeds a throwaway data dir (500 vessels, 10k missions, ...) via src/Armada.PerfSeed, runs a Release Admiral on 127.0.0.1:25060, and reports p50/p95 per endpoint."
timestamp: "2026-10-05T05:28:44.1371670Z"
created: "2026-10-05T05:28:43.6737580Z"
slug: "perf-baseline-script"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["performance-budgets", "test-ports-random-range"]
version: "1"
salience: "0.5"
---
Usage: `scripts/common/perf-baseline.sh [--framework net10.0] [--port 25060] [--runs 50] [--warmup 5] [--output results.tsv] [--keep] [--no-build] [-- <PerfSeed options>]`, for example `-- --missions 50000 --voyages 5000 --vessels 2500 --jobs 20000` for 5x. Builds Armada.Server and src/Armada.PerfSeed in Release, seeds through the database driver (10 fleets, 500 vessels with health rows, 50 idle captains, 1,000 voyages, 10,000 missions with none Pending/Assigned/InProgress so nothing dispatches, 2,000 finished jobs, 300 Ask threads), starts the Admiral with ARMADA_DATA_DIR pointing at it (MCP on port+1), and runs scripts/common/perf-measure.py (5 warmup plus 50 measured requests per endpoint over one connection with a seeded bearer credential; p50, p95, max, bytes; non-zero exit on any error status). Then stops the server and deletes the data unless --keep. Never touches ~/.armada or 7890/7891. Results and budgets are recorded in docs/PERFORMANCE.md (V1_READINESS W4.5). Requires dotnet, python3, curl.
