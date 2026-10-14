---
type: "Reference"
title: "Split-mode container smoke test: e2e-split-mode.sh"
description: "scripts/common/e2e-split-mode.sh builds the server image, boots it --rm in split config, checks health on 7890/7891, tears down; ARMADA_E2E_KEEP=1 keeps it for manual Harbor attach."
timestamp: "2026-10-05T05:29:09.8550940Z"
created: "2026-10-05T05:29:09.6106140Z"
slug: "split-mode-e2e-script"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["harbor-setup-split-mode", "pref-protect-owner-environment"]
version: "1"
salience: "0.5"
---
scripts/common/e2e-split-mode.sh (Windows: scripts/windows/e2e-split-mode.bat) builds the Armada Server image fresh, boots it as a throwaway container with split-mode settings, waits for the unauthenticated health endpoint, asserts the REST/link port 7890 and MCP port 7891 are reachable from the host, then tears down. ARMADA_E2E_KEEP=1 leaves the container running to attach a host Harbor by hand. It does not attach a Harbor or dispatch a real captain; the deterministic in-process equivalent of the split path (real git land through the Harbor seam) is the Services.HarborSplitModeE2E suite. Note this script binds the default ports 7890/7891, so do not run it next to the owner's live Admiral.
