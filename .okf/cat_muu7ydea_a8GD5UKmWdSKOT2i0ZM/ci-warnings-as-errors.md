---
type: "Reference"
title: "CI builds with warnings as errors (NuGet audit excepted)"
description: "Any new compiler warning fails ci.yml; NU1900-NU1904 advisories stay warnings; check locally with -warnaserror before pushing."
timestamp: "2026-10-05T05:26:00.1173560Z"
created: "2026-10-05T05:25:59.3002900Z"
slug: "ci-warnings-as-errors"
category: "cat_muu7ydea_a8GD5UKmWdSKOT2i0ZM"
links: ["build-solution-command", "ci-workflow-ci-yml", "ci-workflow-security", "xml-docs-public-members"]
version: "1"
salience: "0.5"
---
.github/workflows/ci.yml job build-and-test runs `dotnet build src/Armada.sln -c Release --no-restore -warnaserror -warnnotaserror:NU1900,NU1901,NU1902,NU1903,NU1904`. The solution builds with zero warnings, so any new compiler warning (including a missing XML doc comment, since GenerateDocumentationFile is on) fails the build on all three OSes. NuGet audit advisories (NU190x) stay visible but do not fail CI; dependency vulnerabilities are caught by security.yml instead.

Before pushing, reproduce locally: `dotnet build src/Armada.sln -c Release -warnaserror -warnnotaserror:NU1900,NU1901,NU1902,NU1903,NU1904`. docs/RELEASING.md step 2 requires every ci.yml job green for a release commit.
