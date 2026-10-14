---
type: "Project"
title: "Captain routing: preferred captain, persona fences, tier fallback"
description: "Preferred captain = dispatch RequestedCaptainId > voyage per-persona override > Persona.DefaultCaptainId; if idle it runs (ignoring AllowedPersonas); if busy fall back to the lowest idle tier >= mission tier; otherwise AllowedPersonas filter and PreferredPersona bias with soft fallback."
timestamp: "2026-10-05T05:39:21.8774040Z"
created: "2026-10-05T05:39:21.4151750Z"
slug: "arch-captain-routing-persona-tier"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-captain-tier", "term-persona", "arch-mission-assignment"]
version: "1"
salience: "0.5"
---
Code: MissionService.ResolvePreferredCaptainAsync, FindAvailableCaptainAsync(persona, requiredTier, requestedCaptainId), CaptainTierSelector; docs/CAPTAIN_ROUTING.md, docs/SCHEDULING.md.

Preferred captain resolution (stored as Mission.RequestedCaptainId, applies to every mission of that persona in the voyage including Architect fan-out workers): MissionDescription.RequestedCaptainId > voyage CaptainOverridesJson from dispatch captainAssignments [{persona, captainId, fallbackTier}] > Persona.DefaultCaptainId.
Assignment: preferred captain idle -> use it, deliberately ignoring its AllowedPersonas fence; preferred busy -> idle captain with tier >= fallback tier, lowest eligible tier first; preferred deleted -> logged, route normally; nothing satisfies the tier -> stay Pending, retry next tick. Without a preferred captain: idle captains, filter by AllowedPersonas (null = any role), prefer PreferredPersona match, and if no persona-eligible captain is idle fall back to any idle captain (soft constraint). Quarantined/non-Idle captains never qualify. Mission detail shows preferred vs actual captain. Schema columns from migration 55.
