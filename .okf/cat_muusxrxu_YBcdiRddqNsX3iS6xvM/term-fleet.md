---
type: "Reference"
title: "Fleet: a named group of vessels (flt_)"
description: "Fleet (flt_) groups vessels; a \"default\" fleet is auto-created; a fleet may set DefaultPipelineId used when the vessel has none."
timestamp: "2026-10-05T05:28:46.1781530Z"
created: "2026-10-05T05:28:45.6418970Z"
slug: "term-fleet"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-vessel", "arch-pipeline-resolution", "arch-vessel-import-and-categorization"]
version: "1"
salience: "0.5"
---
Term: Fleet (src/Armada.Core/Models/Fleet.cs, prefix flt_). A named collection of vessels (repositories). Fields: TenantId, UserId, Name, Description, DefaultPipelineId, Active. Constants.DefaultFleetName = "default" is created automatically. Fleet 1--* Vessel.

Uses: pipeline resolution falls back to the fleet default after the vessel default ([[arch-pipeline-resolution]]); fleet actions and vessel health can be filtered by fleet; vessel import can ask a captain to recommend fleets and then create/reuse fleets by name ([[arch-vessel-import-and-categorization]]); project/workflow profiles resolve Global -> Fleet -> Vessel. Example: `armada fleet add payments -d "Services that move money"`.
