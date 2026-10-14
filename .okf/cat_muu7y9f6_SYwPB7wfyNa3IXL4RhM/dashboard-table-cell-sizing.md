---
type: "Project"
title: "Dashboard table cell sizing utilities (App.css)"
description: "How dashboard tables avoid horizontal scroll: shrinkable ID/URL grid, cell-clip, line-clamp, cell-title, cell-ident, table-dense."
timestamp: "2026-10-04T19:33:45.2140180Z"
created: "2026-10-04T19:33:41.2015660Z"
slug: "dashboard-table-cell-sizing"
category: "cat_muu7y9f6_SYwPB7wfyNa3IXL4RhM"
version: "1"
salience: "0.5"
---
Armada dashboard tables (src/Armada.Dashboard/src/App.css, branch fix/dashboard-table-sizing, 2026-10) must not scroll horizontally at 1280+ px with the sidebar expanded. Patterns: (1) ID/URL cells use <span class="id-display"><span class="id-value|url-value" title=full>..</span><CopyButton/></span>; inside td the id-display is a grid with first track minmax(var(--cell-min,4.5rem), max-content) so it truncates with ellipsis only when space is short. (2) Single-line paths/branches/IDs without copy: <span class="cell-clip" ><span>full</span></span> with title on the td. (3) Free text one line: .truncate-text / .line-clamp-1 (webkit line clamp, keeps min-content small); titles: td.cell-title + strong.line-clamp-2. (4) PascalCase/dotted identifiers: td.cell-ident (overflow-wrap:anywhere). (5) Many-column tables: table.table-dense. Global: .table-wrap td overflow-wrap:break-word; th white-space normal (headers wrap only when needed). Avoid white-space:nowrap / fixed max-width px in cells: nowrap makes min-content = full text and forces overflow. Vessel Health hides lastCommit/evaluated by default via useTablePrefs defaultHidden + defaultsVersion migration. Mobile (<=768px) .app-layout needs !important to override Layout's inline grid columns.
