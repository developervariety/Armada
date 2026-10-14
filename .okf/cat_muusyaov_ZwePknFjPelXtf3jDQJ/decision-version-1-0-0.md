---
type: "Project"
title: "Version bumped to 1.0.0 (2026-10-05); images built only after green CI"
description: "Owner approved 1.0.0 on 2026-10-05: bump code version first, wait for CI green, then build-all v1.0.0 pushes Docker images."
timestamp: "2026-10-05T05:24:19.3043860Z"
created: "2026-10-05T05:24:18.9995170Z"
slug: "decision-version-1-0-0"
category: "cat_muusyaov_ZwePknFjPelXtf3jDQJ"
links: ["pref-version-changes-need-approval", "open-release-v1-build"]
version: "1"
salience: "0.5"
---
On 2026-10-05 the owner asked to "run build-all v1.0.0" so he could test the TUI. The code still said 0.9.0, so he was asked and chose "Bump, wait for CI, then build": ProductVersion/Directory.Build.props/Helm csproj/dashboard package.json and current-version docs were set to 1.0.0 (commit "Version 1.0.0; ASCII logo on the TUI login screen"), 0.9.0 was added to the ReleaseVersion suite's stale list, and build-all v1.0.0 (Docker Hub push of server and proxy images as v1.0.0 and latest) runs only after the CI run on the pushed commits is green. Note V1_READINESS's W8.2-W8.4 still describe beta/RC/GA steps; the owner chose to tag 1.0.0 now.
