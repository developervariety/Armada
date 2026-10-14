---
type: "Project"
title: "Request history capture and the cross-entity history timeline"
description: "PostRouting captures every REST call into request_history (req_; sanitized headers/bodies up to 32 KB, excluded routes, 30-day retention) for the Activity Requests view and API Explorer replay; HistoricalTimelineService builds GET /api/v1/history from existing records."
timestamp: "2026-10-05T05:44:24.1669770Z"
created: "2026-10-05T05:44:23.7804710Z"
slug: "arch-request-history-and-timeline"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-armada-event", "arch-delivery-subsystem"]
version: "1"
salience: "0.5"
---
Request history: ArmadaServer.PostRouting -> CaptureRequestHistoryAsync -> RequestHistoryCaptureService (src/Armada.Core/Services/RequestHistoryCaptureService.cs) builds sanitized RequestHistoryRecord/Entry/Detail (prefix req_) from raw request/response: settings RequestHistoryEnabled (true), RequestHistoryRetentionDays (30), RequestHistoryMaxBodyBytes (32768), RequestHistoryExcludeRoutes, RequestHistoryCaptureRequestHeaders / ResponseHeaders (true); credential headers are redacted (only headers; bodies such as fleet action output are not scrubbed, which is why output is excluded from list responses). Purged every 100 health cycles. Routes /api/v1/request-history (enumerate, summaries via RequestHistorySummaryBuilder, detail, replay metadata). Dashboard Activity (Requests) and API Explorer (live OpenAPI at /openapi.json and /swagger).
Timeline: HistoricalTimelineService (src/Armada.Core/Services/HistoricalTimelineService.cs) assembles HistoricalTimelineEntry rows across objectives, planning, dispatch, missions, checks, releases, deployments, incidents, runbook executions, merge activity, events, and request history (GET /api/v1/history, HistoricalTimelineQuery filters such as objectiveId), backing Activity (All Activity). CascadeCleanup removes dependent events/planning rows when parents are hard-deleted.
