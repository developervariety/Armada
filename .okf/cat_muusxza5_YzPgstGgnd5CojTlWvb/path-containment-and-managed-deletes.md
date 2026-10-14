---
type: "Reference"
title: "Path containment (PathContainment.IsInside) and managed-directory deletion guard"
description: "Containment uses Path.GetRelativePath (reject rooted or ..), never StartsWith; vessel/dock directories are deleted only when strictly inside ReposDirectory/DocksDirectory, symlinks refused (F-11)."
timestamp: "2026-10-05T05:35:07.4480340Z"
created: "2026-10-05T05:35:07.1806080Z"
slug: "path-containment-and-managed-deletes"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["paths-containment-rule-6", "open-o07-vessel-url-path-validation", "proxy-route-policy"]
version: "1"
salience: "0.5"
---
src/Armada.Core/PathContainment.cs: IsInside(root, candidate, allowRoot) makes both paths absolute with Path.GetFullPath, then accepts only when Path.GetRelativePath(root, candidate) is not rooted and does not start with "..". This replaces string prefix checks, which accepted siblings sharing a prefix (/srv/app vs /srv/app-secrets). Case sensitivity follows the platform. Used (fragility R1c) for proxy and Admiral dashboard static files, CheckRun artifacts and parsing, repository inventory reads, Workspace paths. Test: Services.PathContainment.
F-11: deleting a vessel used to recursively delete vessel.LocalPath and docks/<vessel.Name> (caller controlled), so a tenant admin could delete any directory the Admiral could write; dock provisioning force-removed a non-repo LocalPath. Now directories are removed only when strictly inside ReposDirectory / DocksDirectory (symlinks refused); otherwise left in place with a warning (Services.SecurityHardening managed-path guard). Vessel import browse limits to Import.AllowedRoots (default the Admiral user's home), normalized and checked, reparse points skipped. Fragility rule 6 makes this mandatory for new code.
