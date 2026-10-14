---
type: "Project"
title: "TUI default server profile captured a stale port"
description: "The auto-created default profile stored the URL from settings.json once (while it held test values, :21xxx); it now follows the local Admiral port until edited (FollowsLocalAdmiral)."
timestamp: "2026-10-05T05:26:05.5171750Z"
created: "2026-10-05T05:26:05.0451020Z"
slug: "lesson-tui-default-profile-stale-port"
category: "cat_muusycl1_gCgWgPNmexMeVNveUK6"
version: "1"
salience: "0.5"
---
2026-10-05: the owner saw the TUI default to :21000 instead of :7890. The TUI creates its "default" profile from LocalAdmiralDefaults (port from ~/.armada/settings.json) on first start and never re-read it; it was first created while settings.json held test values. Fix: ServerProfile.FollowsLocalAdmiral (null in old files means true for the loopback profile named default) keeps the profile on the local Admiral's current port at startup when no server/profile is named explicitly; editing the URL with `e` in the Server picker turns following off. Lesson: derived defaults that are persisted must be refreshed from their source or marked as user-set.
