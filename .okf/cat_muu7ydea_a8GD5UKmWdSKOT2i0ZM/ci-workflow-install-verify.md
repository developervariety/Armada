---
type: "Reference"
title: "CI workflow install-verify.yml"
description: "Runs the install-verify scripts (docker, linux deb/rpm, dotnet-tool on ubuntu+macOS, macos-pkg, windows) on pushes to main touching src/docker/packaging, packaging PRs, weekly Monday 06:30, and on demand."
timestamp: "2026-10-05T05:28:12.3612160Z"
created: "2026-10-05T05:28:11.8485220Z"
slug: "ci-workflow-install-verify"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["install-verify-scripts", "release-checklist"]
version: "1"
salience: "0.5"
---
.github/workflows/install-verify.yml (V1 readiness W5.5). Triggers: workflow_dispatch; schedule cron "30 6 * * 1"; push to main on paths src/**, docker/**, scripts/common/install-verify/**, publisher.json, the workflow; pull_request on Armada.Server, Armada.Helm, Armada.Publisher, dashboard Dockerfile/nginx.conf, docker/**, install-verify scripts, publisher.json. Jobs: docker (verify-docker.sh), linux-packages (installs fpm and rpm, then verify-linux-package.sh --format deb and --format rpm into $RUNNER_TEMP/linux-packages), dotnet-tool (ubuntu-latest and macos-latest, verify-dotnet-tool.sh), macos-pkg (verify-macos-pkg.sh), windows (Python 3.12, git longpaths, pwsh verify-windows.ps1; written without a Windows machine, so its first green run is the real check). The most recent run against a release commit must be green.
