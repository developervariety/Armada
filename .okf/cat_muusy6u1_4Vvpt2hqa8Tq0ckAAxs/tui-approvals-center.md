---
type: "Reference"
title: "TUI Approvals center (Ctrl+A): one queue of everything waiting on you"
description: "ApprovalService merges Ask proposals, mission reviews, deployment approvals, failed landings, and stalled captains; single-key decisions (a, r, c, m, d, l, s, R, t) make the same calls and confirmations as the dashboard."
timestamp: "2026-10-05T05:39:21.8856110Z"
created: "2026-10-05T05:39:21.1021270Z"
slug: "tui-approvals-center"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-ask-armada", "tui-notifications", "tui-telemetry"]
version: "1"
salience: "0.5"
---
Services/ApprovalService.cs (ApprovalItem, ApprovalKindEnum) and Approvals/ApprovalsScreen.cs (TUI-only route /approvals, parity status extension). Sources: Ask proposals (ask.proposal events and open conversations), mission reviews (Needs You inbox and mission.changed), deployment approvals (inbox and deployment.changed), failed landings (inbox and mission.changed), stalled captains (inbox and captain.changed). Keys: Ask proposal a approve, r reject, x arguments; mission review a approve, c conditionally approve, m more work required, d deny (Resolve Review dialog; feedback required for c and m; ReviewDecisionModal, ReviewVerdictEnum); deployment a approve, d deny (confirm); failed landing l retry landing; stalled captain s stop, R recall, t restart (confirm). Enter opens the item's own screen, F5 re-reads the inbox. The header shows the count on every screen; new items ring the terminal bell and raise an OS notification in the background when OsNotifications is set; a proposal from a conversation you are not viewing raises a toast whose Ctrl+O opens it. The queue reacts to entity events immediately and follows the inbox poll (20 s). Telemetry records approval decisions and latency.
