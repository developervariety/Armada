---
type: "Project"
title: "Mission scheduling order: priority, voyage tiebreak, FIFO"
description: "Pending missions are assigned lowest Priority number first (default 100), active-voyage missions win ties over standalone ones, then creation order; claims are atomic; nothing is dropped while captains are busy."
timestamp: "2026-10-05T05:39:14.8796640Z"
created: "2026-10-05T05:39:14.4016890Z"
slug: "arch-mission-scheduling"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-mission-assignment", "arch-captain-routing-persona-tier"]
version: "1"
salience: "0.5"
---
docs/SCHEDULING.md; code AdmiralService.DispatchPendingMissionsAsync / MissionService.DispatchPendingMissionsAsync.

Order: (1) Priority ascending (1-10 urgent, 50 high, 100 default, 200+ background); (2) within a priority, missions belonging to an active/in-progress voyage before standalone missions; (3) FIFO by creation time. Assignment uses an atomic TryClaim so two idle captains cannot take the same mission; a failed claim moves to the next mission. Missions wait in Pending until a captain frees up; checked on the next heartbeat (HeartbeatIntervalSeconds, code default 10 s; SCHEDULING.md still says 30). Dispatch also respects MaxConcurrentMissions (global ceiling, 0 = unlimited), MaxCaptains (0 = auto = 5), per-vessel serialization, dependency gates, and the memory admission floor. Set priority with `armada go --priority 1`, MCP create_mission/dispatch priority, update_mission priority.
