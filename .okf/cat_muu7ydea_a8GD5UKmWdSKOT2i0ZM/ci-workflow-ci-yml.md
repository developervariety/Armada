---
type: "Reference"
title: "CI workflow ci.yml: build-and-test matrix, dashboard, compose"
description: "On every push/PR: 3 OS x {net8.0, net10.0} warning-free Release build plus full Test.Automated; dashboard npm ci/build/test plus dist drift check; compose file validation."
timestamp: "2026-10-05T05:28:07.2790880Z"
created: "2026-10-05T05:28:06.3737650Z"
slug: "ci-workflow-ci-yml"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["ci-warnings-as-errors", "dashboard-build-and-test-commands", "docker-compose-stack", "lesson-ci-new-test-portability"]
version: "1"
salience: "0.5"
---
.github/workflows/ci.yml (push, pull_request, workflow_dispatch; concurrency cancels in-progress runs per ref):
- build-and-test: matrix os [windows-latest, macos-latest, ubuntu-latest] x framework [net8.0, net10.0], fail-fast false, 90 min timeout. Sets up .NET 8.0.x and 10.0.x, configures git (user Armada CI, ci@armada.invalid, init.defaultBranch main, core.longpaths true), restores, builds Release with -warnaserror (NU1900-NU1904 excepted), runs `dotnet run --project src/Test.Automated -c Release --no-build --framework <fw> -- --results test-results/test-automated-<os>-<fw>.json`, uploads test-results-<os>-<fw> artifacts always.
- dashboard (ubuntu, Node 24): npm ci --no-audit --no-fund, npm run build, npm run test:run, then fails if src/Armada.Dashboard/dist differs from the fresh build.
- compose (ubuntu): `docker compose config --quiet` on docker/armada/compose.yaml, compose.split.yaml, and docker/proxy/compose.yaml with placeholder ARMADA_INITIAL_ADMIN_PASSWORD / ARMADA_PROXY_PASSWORD, then proves each file still refuses to render when its secret is unset.
Windows-only and macOS-only failures are common for new tests (paths, CRLF, timing).
