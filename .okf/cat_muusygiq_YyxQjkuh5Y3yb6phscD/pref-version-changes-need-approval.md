---
type: "Feedback"
title: "Never change version numbers without explicit owner approval"
description: "Version bumps (ProductVersion, Directory.Build.props, Helm csproj, dashboard package.json) only on explicit approval; 1.0.0 was approved 2026-10-05."
timestamp: "2026-10-05T05:22:49.3807890Z"
created: "2026-10-05T05:22:48.9891780Z"
slug: "pref-version-changes-need-approval"
category: "cat_muusygiq_YyxQjkuh5Y3yb6phscD"
links: ["decision-version-1-0-0", "pref-confirm-outward-actions"]
version: "1"
salience: "0.5"
---
Rule: do not change Armada's version without the owner explicitly asking. Version lives in src/Armada.Core/Constants.cs (ProductVersion), src/Directory.Build.props, src/Armada.Helm/Armada.Helm.csproj, src/Armada.Dashboard/package.json (+ package-lock), plus current-version doc headers (docs/REST_API.md, MCP_API.md, WEBSOCKET_API.md, PROXY_API.md, REMOTE_MGMT.md, TUNNEL_*.md), example payloads, the Postman collection, and the README banner; Services.ReleaseVersion tests enforce consistency and list stale versions. On 2026-10-05 the owner asked to "run build-all v1.0.0" and, when asked, chose: bump the code to 1.0.0 first, wait for CI to be green, then run build-all (which pushes Docker images). Historical mentions (upgrade notes, migrations/*v0.9.0*) stay unchanged.
