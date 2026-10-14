---
type: "Reference"
title: "Job (job_): pollable background job"
description: "Job (job_) = request-independent background work with Kind (Generic, Cleanup, Sync, Report, VesselDiscovery, VesselImport, FleetCategorization) and Status Queued -> Running -> Succeeded/Failed/Cancelled."
timestamp: "2026-10-05T05:32:59.6370880Z"
created: "2026-10-05T05:32:59.2339990Z"
slug: "term-job"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-vessel-health-evaluator", "arch-vessel-import-and-categorization"]
version: "1"
salience: "0.5"
---
Term: Job (src/Armada.Core/Models/Job.cs, prefix job_; JobService, JobStateMachine). Fields: Name, Kind (JobKindEnum: Generic, Cleanup, Sync, Report, VesselDiscovery, VesselImport, FleetCategorization), Status (JobStatusEnum), progress counter, ResultJson, ErrorReason, Created/Started/Completed/LastUpdate timestamps. All reported fields are typed columns.

Transitions (JobStateMachine): Queued -> Running or Cancelled; Running -> Succeeded, Failed, or Cancelled; terminal states never change. Users: vessel health evaluation (kind Report, one per tenant at a time; a second request returns the running job id with 409), vessel discovery/import, fleet categorization. JobService.MaintainAsync runs every health tick and fails jobs whose worker died; restart recovery fails interrupted import/categorization jobs. Retention.JobRetentionDays (30) prunes finished jobs but keeps the newest finished job of each kind+name per tenant (schedules measure from it). REST GET /api/v1/jobs (shape { Success, Objects, TotalRecords }), GET /jobs/{id}, POST /jobs/{id}/cancel; MCP enumerate entityType jobs; tracked by Ask threads.
