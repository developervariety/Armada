---
type: "Reference"
title: "TUI notifications: toasts, notification center, bell, OS notifications, EventPump"
description: "Toasts mirror the dashboard's wording and severity for mission/voyage/captain/deployment/backlog/incident changes; Ctrl+N center keeps the latest 100 across runs; bell rings for failures and approvals; OsNotifications Osc9/Osc777/Native."
timestamp: "2026-10-05T05:39:28.2693220Z"
created: "2026-10-05T05:39:27.9017220Z"
slug: "tui-notifications"
category: "cat_muusy6u1_4Vvpt2hqa8Tq0ckAAxs"
links: ["tui-approvals-center", "lesson-script-editor-toasts", "decision-notification-command-runner"]
version: "1"
salience: "0.5"
---
Services/NotificationService.cs (NotificationEntry, ToastEntry, NotificationSeverityEnum), Services/EventPump.cs, Services/OsNotifier.cs (OsNotificationModeEnum). EventPump owns the ArmadaSocket (token auth, subscribe, reconnect with backoff 1-30 s and jitter), decodes typed events, fans them out to subscribers, marshals every UI mutation through the UI dispatcher, and coalesces bursts (one refresh per screen per 250 ms). Toasts appear top right with the dashboard's wording and severity (e.g. Mission "Fix tables" - Landed as success, Captain "claude-1" - Stalled as warning), auto-dismiss after 5 s, and are kept in history with a deep link. Ctrl+O runs the newest toast's action. Ctrl+N opens the notification center: latest 100, persisted between runs, m marks all read, x twice clears. The terminal bell rings for failures and approvals. OsNotifications in tui.json (Osc9, Osc777, or Native via notify-send/osascript) raises OS notifications while the terminal is unfocused. In tests desktop notifications go through RecordingNotificationCommandRunner, never real osascript.
