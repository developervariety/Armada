---
type: "Reference"
title: "Signal: message between Admiral and captains (sig_)"
description: "Signal (sig_) = message with Type Assignment, Progress, Completion, Error, Heartbeat, Nudge, or Mail; FromCaptainId/ToCaptainId, Payload, Read flag."
timestamp: "2026-10-05T05:29:30.1266780Z"
created: "2026-10-05T05:29:29.7982870Z"
slug: "term-signal"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["rt-armada-protocol-lines", "term-armada-event"]
version: "1"
salience: "0.5"
---
Term: Signal (src/Armada.Core/Models/Signal.cs, prefix sig_). A message between the admiral and captains or between captains. Fields: TenantId, UserId, FromCaptainId, ToCaptainId, Type (SignalTypeEnum: Assignment, Progress, Completion, Error, Heartbeat, Nudge, Mail), Payload, Read, CreatedUtc.

MissionService.TryAssignAsync creates an Assignment signal when it assigns a mission. Operators/orchestrators send signals with MCP send_signal or REST POST /api/v1/signals; read state via PUT /api/v1/signals/{id}/read; GET /api/v1/signals/recent and /signals/recipient/{captainId} return plain arrays. Enumerate filters: signalType, toCaptainId, unreadOnly; MCP enumerate hides Message unless includeMessage=true (messageLength hint). Signals are an owned (Category A) entity, scoped per user/tenant. Old signals are pruned by dataRetentionDays (SQLite only). Note: agent progress is mostly carried by [ARMADA:*] output lines, not signals ([[rt-armada-protocol-lines]]).
