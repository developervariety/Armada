---
type: "Project"
title: "V1 release status and remaining human checks (2026-10-05)"
description: "v1.0.0 images pushed from 7f77eb4b (CI green); remaining items need human eyes or decisions before archiving V1_READINESS.md and TUI_APP_PLAN.md."
timestamp: "2026-10-05T12:47:41.6313270Z"
created: "2026-10-05T05:26:54.2863450Z"
slug: "open-v1-readiness-remaining"
category: "cat_muusyecr_sLYyb3rdwRqCNn0kT8M"
version: "2"
salience: "0.5"
---
2026-10-05: build-all v1.0.0 ran from main 7f77eb4b after ci, install-verify, and security were all green; jchristn77/armada-server and armada-proxy v1.0.0 + latest are on Docker Hub. All engineering items and simulated-testing S2/S3 findings (F1-F19, F37) are fixed; F20-F22, F26 fixed. Open S4 polish: F23-F25, F27-F36 (except F26), F38. Remaining human checks/decisions: real captain session + timed 10-minute onboarding (W6.1/W6.7); terminal visual checks (Windows Terminal, conhost, iTerm2, Terminal.app, GNOME Terminal, SSH, tmux) (W8.4); browser eyeballing of new dashboard UI (shared vessel form, Needs You approval groups, Pending "why waiting" card, Merge in Manage Branches, wizard failure panel); refinement chat after Apply now ends the session; Claude Code missions with --strict-mcp-config (Mcp.MissionScopedTokens); self password change and generated tenant admin password; signed dmg/pkg on a clean Mac and Windows installers; Apple certificates, external security review (W1.8), Gemini guide, translation review; decisions: Mcp.AllowUnauthenticatedLoopback, F36 proxy-only URL probe, deployment approval naming (environment vs title), MCP inbox listing Ask proposals; TUIKit release for U1-U10; Harbor disconnect leaves missions to stall detection. Owner should restart local Admiral (settings hostname reset) and revoke the temporary GitHub PAT.
