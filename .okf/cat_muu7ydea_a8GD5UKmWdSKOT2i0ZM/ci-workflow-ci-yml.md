---
type: "Reference"
title: "CI workflow ci.yml: quick smoke on push; full.yml is the manual full run"
description: "Push/PR: Linux warning-free build + Models,Client,Metrics,Database,Runtimes (~1 min) + dashboard dist check. Everything else is manual (full.yml)."
timestamp: "2026-10-15T15:24:13.0408335Z"
created: "2026-10-05T05:28:06.3737650Z"
slug: "ci-workflow-ci-yml"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["ci-warnings-as-errors", "ci-workflow-install-verify", "ci-workflow-security", "db-parity-script"]
version: "2"
salience: "0.5"
---
Since 2026-10-15 the owner wants pushes to run only a quick smoke test and nothing on a schedule.

.github/workflows/ci.yml (push, pull_request, workflow_dispatch) has two jobs:
- smoke (ubuntu): .NET 8 and 10, git config, restore, a Release build with -warnaserror (NU1900-NU1904 excepted), then `dotnet run --project src/Test.Automated -c Release --no-build --framework net10.0 -- --suites Models,Client,Metrics,Database,Runtimes`. That is about 935 tests, roughly a minute.
- dashboard-dist (ubuntu, Node 24): npm ci and npm run build, then it fails if src/Armada.Dashboard/dist differs.

.github/workflows/full.yml (workflow_dispatch only) is the old full CI:
- the build-and-test matrix: 3 OSes x net8.0/net10.0, every suite;
- dashboard build, Vitest and the dist check;
- tui-parity;
- mobile: typecheck, lint, Jest, routes, parity;
- compose validation.

Run full.yml before a release (docs/RELEASING.md). Keep new suites that are slow, or that need Docker or a network, out of the smoke prefixes.
