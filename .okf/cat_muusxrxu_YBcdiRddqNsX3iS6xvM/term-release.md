---
type: "Reference"
title: "Release (rel_): aggregated record of what ships"
description: "Release (rel_) links voyages, missions, check runs, version/tag, notes, and artifacts; statuses Draft, Candidate, Shipped, Failed, RolledBack."
timestamp: "2026-10-05T05:32:18.8721370Z"
created: "2026-10-05T05:32:18.4557930Z"
slug: "term-release"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-deployment-environment", "arch-delivery-subsystem"]
version: "1"
salience: "0.5"
---
Term: Release (src/Armada.Core/Models/Release.cs, prefix rel_; ReleaseService). A first-class release record under Delivery > Releases. Status ReleaseStatusEnum: Draft, Candidate, Shipped, Failed, RolledBack. Holds linked vessel, voyages, missions, check runs, objectives, derived version (via workflow profile ReleaseVersioningCommand), tag, summary, notes, artifacts (ReleaseArtifact). Can be drafted from objectives, voyages, checks, or directly, and refreshed when new linked checks/artifacts appear. Release detail can show GitHub PR review/check evidence (GET /api/v1/releases/{id}/github/pull-requests, pull-based, token never exposed). Deployments can reference a release. REST /api/v1/releases; MCP create_release, get_release. See [[term-deployment-environment]], [[arch-delivery-subsystem]].
