---
type: "Reference"
title: "Operational troubleshooting playbook"
description: "Address in use, health never green, /dashboard 404 or stale, Pending forever in split mode, Harbor captain cannot reach MCP, captain stuck Working, Docker permission errors, restore not visible, npm SELF_SIGNED_CERT."
timestamp: "2026-10-05T05:32:50.2163600Z"
created: "2026-10-05T05:32:30.4870920Z"
slug: "troubleshooting-common"
category: "cat_muusxwq7_OhERdIVgGl16Il44xf7"
links: ["logs-layout", "ports-and-listeners", "dashboard-served-by-admiral", "harbor-setup-split-mode", "proxy-operations"]
version: "1"
salience: "0.5"
---
From docs/OPERATIONS.md:
- Address in use at start: another process (often a second Armada) holds 7890/7891; stop it or move admiralPort/mcpPort and export ARMADA_BASE_URL.
- Scripts say never healthy: healthcheck-server polls /api/v1/status/health about 30 s; curl it yourself; read the end of admiral.log; a long migration on a large DB can exceed the window.
- /dashboard 404 or old: the Admiral serves dashboardPath, else <dataDir>/dashboard, else dashboard/ next to the exe, else the embedded legacy wwwroot; rerun scripts/common/deploy-dashboard.sh or point dashboardPath at src/Armada.Dashboard/dist.
- Missions Pending forever (split): requireHarborForLaunch needs a connected Harbor of the same user advertising the runtime; Degraded/Disconnected means heartbeats stopped (45 s).
- Harbor captain cannot reach MCP: curl harbor.advertisedMcpBaseUrl from the Harbor host.
- Captain stuck Working: watchdog marks stalled after stallThresholdMinutes and retries up to maxRecoveryAttempts; check captains/ and missions/<id>.log; expired agent login or usage limit is common; captain quarantined for captainQuarantineMinutes.
- Docker DB permission errors: chown to 1654.
- Restore succeeded but old data: restart the server.
- npm SELF_SIGNED_CERT_IN_CHAIN: pass --insecure to the script.
- Tunnel not visible in proxy: see TUNNEL_OPERATIONS failure modes.
