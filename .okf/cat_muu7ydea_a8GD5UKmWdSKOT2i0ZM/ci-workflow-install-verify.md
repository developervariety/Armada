---
type: "Reference"
title: "CI workflow install-verify.yml"
description: "Runs the install-verify scripts (docker, linux deb/rpm, dotnet-tool on ubuntu+macOS, macos-pkg, windows); manual only (workflow_dispatch) since 2026-10-15."
timestamp: "2026-10-15T15:25:00.0000000Z"
created: "2026-10-05T05:28:11.8485220Z"
slug: "ci-workflow-install-verify"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["install-verify-scripts", "release-checklist"]
version: "2"
salience: "0.5"
---
.github/workflows/install-verify.yml (V1 readiness W5.5). Trigger: workflow_dispatch only (since 2026-10-15 the owner wants no scheduled runs and only a quick smoke test on push; see ci-smoke-only-no-schedules). Jobs: docker (verify-docker.sh), linux-packages (installs fpm and rpm, then verify-linux-package.sh --format deb and --format rpm into $RUNNER_TEMP/linux-packages), dotnet-tool (ubuntu-latest and macos-latest, verify-dotnet-tool.sh), macos-pkg (verify-macos-pkg.sh), windows (Python 3.12, git longpaths, pwsh verify-windows.ps1; written without a Windows machine, so its first green run is the real check). The most recent run against a release commit must be green.
