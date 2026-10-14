---
type: "Reference"
title: "Install verification scripts (scripts/common/install-verify)"
description: "verify-docker.sh, verify-linux-package.sh --format deb|rpm, verify-dotnet-tool.sh, verify-macos-pkg.sh, verify-windows.ps1 install each 1.0 path and land one mission on a stub-inference ApiEndpoint captain."
timestamp: "2026-10-05T05:27:59.7432970Z"
created: "2026-10-05T05:27:59.1946140Z"
slug: "install-verify-scripts"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["ci-workflow-install-verify", "install-channels-1-0", "stub-agent-runtimes-in-tests"]
version: "1"
salience: "0.5"
---
Each script installs one supported path and drives it end to end via smoke.py (stdlib Python 3.8+): Admiral healthy, /dashboard serves the React build, admin@armada logs in via POST /api/v1/authenticate and GET /api/v1/whoami works, a fleet and a vessel are created from a temp bare git repo, one voyage with one mission is dispatched on an ApiEndpoint captain whose endpoint is stub_inference.py (OpenAI-compatible stub: write_file INSTALL_SMOKE.md, then run_process git add/commit, then finish). The mission must reach WorkProduced or later and its diff must contain the file.

Scripts: verify-docker.sh (docker/armada/compose.yaml built from the checkout under a throwaway project), verify-linux-package.sh --format deb (ubuntu:24.04) / --format rpm (fedora:42) (builds with fpm in a container if missing), verify-dotnet-tool.sh (Armada.Helm packed and installed with --tool-path, then armada server start/stop), verify-macos-pkg.sh (pkg payload expanded into a temp root), verify-windows.ps1 (tool plus Armada.Server.exe --install-service --dry-run). Shared helpers in lib.sh.

Safety: never touch ~/.armada, the global dotnet tool dir, docker/armada/db, or system locations; temp ARMADA_DATA_DIR and HOME; bind only 127.0.0.1 ports 34000-34100 (IV_PORT_BASE); service flags only with --dry-run; --keep keeps temp state. Not covered: msi/Inno installers, Harbor packages, real service registration, CLI captains.
