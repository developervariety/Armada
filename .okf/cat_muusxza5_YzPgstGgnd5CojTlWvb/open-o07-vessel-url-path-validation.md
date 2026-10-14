---
type: "Project"
title: "O-07 (Medium, open): vessel RepoUrl, LocalPath, WorkingDirectory not validated"
description: "file:// and local paths clone any repo the Admiral can read, a RepoUrl starting with \"-\" risks git option injection, bare repo hooks run, import browse defaults to home and does not resolve symlinks; untrusted repos can run MSBuild props in health checks."
timestamp: "2026-10-05T05:36:26.8433360Z"
created: "2026-10-05T05:36:26.6432940Z"
slug: "open-o07-vessel-url-path-validation"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["path-containment-and-managed-deletes", "security-review-living-document"]
version: "1"
salience: "0.5"
---
Applies to vessel create/update (REST, MCP, WebSocket; TenantAdmin), vessel import browse/discover/import, and vessel health evaluation. Issues: no allow-list for RepoUrl/LocalPath/WorkingDirectory, so file:// URLs and local paths clone any repository the Admiral user can read; a RepoUrl starting with "-" is a possible git option injection; a prepared bare repository's hooks run during worktree operations; vessel import browse defaults to the Admiral user's home (Import.AllowedRoots) and does not resolve symlinks in the requested path; repository-controlled MSBuild props can run during `dotnet list package` in health evaluation. Planned W1 follow-up: path and URL allow-lists and `--` before user arguments to git. Status 2026-10-04: open. When touching git invocations with user-supplied values, put `--` before them now.
