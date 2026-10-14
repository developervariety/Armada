---
type: "Project"
title: "Harbor settings.json keys are PascalCase and case-sensitive; link is on port 7890"
description: "HarborAppSettings uses default System.Text.Json (PascalCase, case-sensitive), so camelCase keys in docs/HARBOR.md are ignored and overwritten; default ServerLinkUrl is ws://127.0.0.1:7890/v1.0/harbor/connect, not 7891."
timestamp: "2026-10-05T05:36:12.3076960Z"
created: "2026-10-05T05:36:12.0561900Z"
slug: "harbor-settings-pascalcase-and-link-port"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
version: "1"
salience: "0.5"
---
Found 2026-10-04 while updating the README for v1.0. src/Armada.Harbor/HarborAppSettings.cs loads and saves with JsonSerializer default options (no naming policy, no PropertyNameCaseInsensitive), so ~/.armada-harbor/settings.json is written in PascalCase (ServerLinkUrl, AccessKey, Capabilities, MaxConcurrentJobs, ...) and camelCase keys are silently ignored, then overwritten on the next Save() at startup. docs/HARBOR.md still documents camelCase keys and a default link URL on port 7891; the code default is ws://127.0.0.1:7890/v1.0/harbor/connect (the link lives on the Admiral port, per docs/OPERATIONS.md and compose.split.yaml). The README (branch work/readme-v1, commit b3c2c9ba) now uses PascalCase and 7890. Open: fix docs/HARBOR.md (or make Harbor read case-insensitively).
