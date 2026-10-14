---
type: "Reference"
title: "Captain tier: Economy, Standard, Premium capability levels"
description: "CaptainTierEnum Economy=0 < Standard=1 < Premium=2 orders captains cheapest to strongest; missions can carry a fallback Tier so a busy preferred captain falls back to the lowest eligible tier at or above it."
timestamp: "2026-10-05T05:33:17.7183770Z"
created: "2026-10-05T05:33:17.4305070Z"
slug: "term-captain-tier"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-captain-routing-persona-tier", "term-captain"]
version: "1"
salience: "0.5"
---
Term: Captain tier (Captain.Tier, Mission.Tier, CaptainTierEnum Economy=0, Standard=1, Premium=2; selector src/Armada.Core/Services/CaptainTierSelector.cs). Tier is the lever that makes capability fallback meaningful.

When a mission's preferred captain (RequestedCaptainId) is busy, assignment falls back to an idle captain at or above the mission's fallback tier, preferring the LOWEST eligible tier so Premium captains are not spent on routine work; if none qualifies the mission stays Pending and is retried next tick. Tier comes from dispatch captainAssignments[].fallbackTier or a per-mission tier. Recommended pattern ("roles times tiers"): several Economy captains fenced to Worker/Test Engineer, one or two Premium captains for Architect/Judge. The setup wizard asks each captain's tier. Quarantined captains are excluded from tier selection. See [[arch-captain-routing-persona-tier]].
