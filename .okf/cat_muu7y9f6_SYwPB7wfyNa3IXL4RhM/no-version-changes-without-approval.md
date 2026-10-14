---
type: "Project"
title: "Never change version numbers without the owner's explicit approval"
description: "Do not bump Version in Directory.Build.props, the Helm csproj, Constants.ProductVersion, dashboard package.json/lock, build scripts, Postman collection, CHANGELOG heading, or TUIKit; propose and wait."
timestamp: "2026-10-05T05:44:17.7132630Z"
created: "2026-10-05T05:44:15.2170440Z"
slug: "no-version-changes-without-approval"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["pref-version-changes-need-approval", "decision-version-1-0-0", "release-checklist"]
version: "1"
salience: "0.5"
---
docs/RELEASING.md: "version numbers change only on the maintainer's explicit instruction." Locations: src/Directory.Build.props <Version> (1.0.0), src/Armada.Helm/Armada.Helm.csproj, src/Armada.Core/Constants.cs ProductVersion, src/Armada.Dashboard/package.json and package-lock.json, build scripts, Armada.postman_collection.json, the CHANGELOG heading, and docs current-version headers. The rule comes from VERSIONING.md in the shared requirements (TUI_APP_PLAN.md, V1_READINESS.md, CODEHUB_CAPABILITIES.md repeat it); the TUIKit upstream branch kept its version unchanged for the same reason. Version 1.0.0 was set by the owner (commit ce7357d8). Agents may propose a bump with reasons, then wait.
