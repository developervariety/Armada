---
type: "Reference"
title: "CI workflow security.yml: NuGet and npm vulnerability scanning"
description: "Fails on any vulnerable NuGet package (including transitive) or an npm advisory of high/critical in the dashboard; manual only (workflow_dispatch) since 2026-10-15; run before releases."
timestamp: "2026-10-15T15:25:00.0000000Z"
created: "2026-10-05T05:28:16.7652260Z"
slug: "ci-workflow-security"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["ci-warnings-as-errors", "dependency-scanning-control"]
version: "2"
salience: "0.5"
---
.github/workflows/security.yml (V1_READINESS W1.7, security finding F-22). Trigger: workflow_dispatch only (since 2026-10-15: a new advisory is not a defect in the pushed change, and nothing runs on a schedule; run it before a release). Jobs:
- nuget (ubuntu): dotnet restore src/Armada.sln; `dotnet list src/Armada.sln package --vulnerable --include-transitive`; fails if the output contains "has the following vulnerable packages".
- npm (ubuntu, Node 22, working dir src/Armada.Dashboard): `npm audit --audit-level=high`.
Fix by upgrading the package (or lockfile) rather than suppressing. Docker base images are pinned to exact patch tags and bumped deliberately.
