---
type: "Project"
title: "O-09 (Medium, open): environment health and verification URLs allow SSRF"
description: "Deployment environment health and verification URLs make server-side HTTP requests to any address, including internal and cloud metadata addresses. Owner: Delivery."
timestamp: "2026-10-05T05:36:36.6255780Z"
created: "2026-10-05T05:36:36.4844230Z"
slug: "open-o09-environment-url-ssrf"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["security-review-living-document", "audit-command-events"]
version: "1"
salience: "0.5"
---
Deployments and environments (TenantAdmin; POST /api/v1/deployments and environment health probes) make server-side HTTP requests to user-configured health and verification URLs without restriction, so a tenant admin can reach internal services or link-local/cloud metadata endpoints from the Admiral. Planned fix (owner: Delivery): URL allow-list and blocking of link-local and metadata addresses. Status 2026-10-04: open. Deployment, verification, and rollback commands run through check runs and are audited (F-16).
