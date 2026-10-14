---
type: "Reference"
title: "EnumerationQuery / EnumerationResult: the data-layer paging contract"
description: "EnumerationQuery (PageNumber >= 1, PageSize default 10 clamped 1-1000, Order CreatedAscending/CreatedDescending, Created After/Before, Status, FleetId, VesselId, CaptainId, VoyageId, MissionId, EventType, SignalType, ToCaptainId, UnreadOnly, UserId, TenantId) -> EnumerationResult<T> {Success, PageNumber, PageSize, TotalPages, TotalRecords, Objects, TotalMs}."
timestamp: "2026-10-05T05:47:34.8342300Z"
created: "2026-10-05T05:47:34.4105120Z"
slug: "data-enumeration-query"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["api-rest-pagination", "data-methods-pattern"]
version: "1"
salience: "0.5"
---
Models: src/Armada.Core/Models/EnumerationQuery.cs (setters clamp: page < 1 -> 1; size < 1 -> 1, > 1000 -> 1000; default size 10; default order CreatedDescending via EnumerationOrderEnum) and EnumerationResult.cs. Every I*Methods has EnumerateAsync(EnumerationQuery) plus tenant/user-scoped overloads; drivers translate filters to WHERE clauses with parameters and compute TotalRecords/TotalPages; ordering is by created_utc only. The same shape is returned by REST GET list routes, POST /<entity>/enumerate (query string overrides body), MCP enumerate, and WebSocket list_* commands. Some subsystems page in memory instead (MemoryService search; vessel health enumeration with its own VesselHealthQuery/VesselHealthQueryBuilder for sort/filter). A few routes are deliberately unpaged (see [[api-rest-pagination]]).
