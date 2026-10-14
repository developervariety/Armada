---
type: "Project"
title: "O-02 (High, partly closed): captain chat and planning keep the captain's auto-approve"
description: "Ask turns are fixed (F-27), but direct captain chat and planning (TenantAdmin) still use the captain's own auto-approve setting, and acceptEdits mode can still edit files in the Ask temp dir. Owner: W6.3."
timestamp: "2026-10-05T05:35:59.7623940Z"
created: "2026-10-05T05:35:59.4136120Z"
slug: "open-o02-ask-chat-planning-residual"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["ask-captain-auto-approve-off", "captain-auto-approve-flags", "security-review-living-document"]
version: "1"
salience: "0.5"
---
Finding: any authenticated user could start an Ask thread turn that ran a CLI captain on the Admiral host with that captain's auto-approve flags. Closed in W1.9 for Ask turns (ask.captainAutoApprove default false). Residual as of 2026-10-04: POST /api/v1/captains/{id}/chat and planning sessions (TenantAdmin) still launch with the captain's own setting; in acceptEdits mode Claude Code may still edit files and run file-system commands inside the turn's temporary working directory; other runtimes keep their documented auto-approve-off behavior (Codex --sandbox workspace-write, Gemini auto_edit, Mux deny). Owner: W6.3. Mitigation today: turn off autoApprove on captains used for chat/planning, or restrict those to trusted tenant admins.
