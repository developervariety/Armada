---
type: "Reference"
title: "Captain auto-approve flags per runtime and the per-captain/per-vessel switch"
description: "CLI captains default to bypass flags (Claude --dangerously-skip-permissions, Codex --full-auto, Gemini --approval-mode yolo, Cursor --force, OpenCode --auto, Mux --yolo); captain autoApprove and Vessel.AutoApprove (wins) turn them off."
timestamp: "2026-10-05T05:34:32.0155230Z"
created: "2026-10-05T05:34:31.6754950Z"
slug: "captain-auto-approve-flags"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["ask-captain-auto-approve-off", "audit-command-events", "harbor-link-authentication"]
version: "1"
salience: "0.5"
---
docs/SECURITY_REVIEW.md "Running agents safely" (F-23 mitigated, F-28 fixed). Default flags and the autoApprove=false equivalents:
- Claude Code: --dangerously-skip-permissions / --permission-mode acceptEdits --allowedTools mcp__armada
- Codex: --full-auto (--dangerously-bypass-approvals-and-sandbox on Windows) / --sandbox workspace-write
- Gemini: --approval-mode yolo / --approval-mode auto_edit
- Cursor: --force / no --force
- OpenCode: --auto / no --auto
- Mux: --yolo / --approval-policy deny (an explicit captain Mux policy wins)
- ApiEndpoint: not applicable (tools run in-process through Armada).
Controls: per captain (dashboard "Auto-approve agent tool use", MCP create_captain/update_captain autoApprove, REST runtimeOptionsJson {"autoApprove": false}); per vessel Vessel.AutoApprove (null uses the captain setting; true or false wins; MCP add_vessel/update_vessel autoApprove and clearAutoApprove; migration 76). Harbor launches carry the resolved decision. With auto-approve on, a captain can do anything the Armada OS account can. Recommendations: dedicated unprivileged OS account or container, turn auto-approve off where unattended shell is not needed, Harbors on separate machines for untrusted repos (requireHarborForLaunch), keep the Admiral on localhost, review audit.command events.
