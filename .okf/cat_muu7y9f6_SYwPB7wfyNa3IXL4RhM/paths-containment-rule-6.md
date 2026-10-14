---
type: "Project"
title: "Fragility rule 6: path containment via Path.GetRelativePath; route policies on canonical paths"
description: "Check containment with PathContainment.IsInside (GetRelativePath, reject rooted and ..), never string StartsWith; route policies match canonicalized paths or templates."
timestamp: "2026-10-05T05:45:18.1674240Z"
created: "2026-10-05T05:45:17.7991380Z"
slug: "paths-containment-rule-6"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
links: ["path-containment-and-managed-deletes", "proxy-route-policy"]
version: "1"
salience: "0.5"
---
FRAGILITY_REMEDIATION.md rule 6 (R1c done 2026-10-04). Use src/Armada.Core/PathContainment.cs for every "is this path inside that root" decision (static files, artifacts, workspace, inventory, deletions); StartsWith accepts /srv/app-secrets as inside /srv/app. Route or URL policies must canonicalize first (UrlPathCanonicalizer: percent-decode, collapse slashes, drop trailing slash, reject ambiguous encodings and dot segments) and match templates on segments, as ProxyRoutePolicyService does; the dashboard prefix check uses a segment boundary.
