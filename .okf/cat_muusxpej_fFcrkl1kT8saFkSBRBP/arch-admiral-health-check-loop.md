---
type: "Project"
title: "Admiral health-check loop: what runs every heartbeat and on slow cadences"
description: "ArmadaServer.HealthCheckLoopAsync runs every HeartbeatIntervalSeconds (code default 10; SCHEDULING.md's 30 is stale): AdmiralService.HealthCheck, rollout windows, merge queue, jobs, vessel health schedule, mission recovery, fleet action sync; every 10 cycles log rotation/sessions/endpoints; every 100 cycles expiry/retention."
timestamp: "2026-10-05T05:38:39.3359110Z"
created: "2026-10-05T05:38:38.8913680Z"
slug: "arch-admiral-health-check-loop"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["arch-admiral-service-health-check", "rt-stall-detection", "arch-merge-queue-service", "arch-autonomous-mission-recovery"]
version: "1"
salience: "0.5"
---
src/Armada.Server/ArmadaServer.cs HealthCheckLoopAsync. On start: AdmiralService.CleanupStaleCaptainsAsync (reset Working captains with dead processes, PID-identity checked), then an immediate HealthCheckAsync + DeploymentService.MonitorRolloutWindowsAsync.
Every tick (Task.Delay(HeartbeatIntervalSeconds*1000); Constants.DefaultHeartbeatIntervalSeconds = 10):
- AdmiralService.HealthCheckAsync ([[arch-admiral-service-health-check]])
- DeploymentService.MonitorRolloutWindowsAsync
- MergeQueueService.ProcessQueueAsync (auto-enqueued entries land without a manual trigger)
- JobService.MaintainAsync (reap jobs whose worker died)
- VesselHealthService.RunScheduleAsync (per tenant every RepositoryHealth.IntervalMinutes, measured from the last job)
- MissionRecoveryCoordinator.MaintainAsync (autonomous rescue)
- FleetActionRunner.SyncMissionRunsAsync (follow Mission-run voyages, dispatch paced targets)
Every 10 cycles: LogRotation of <LogDirectory>/captains and admiral.log; planning and refinement session maintenance; ModelEndpointService.CheckHealthAllAsync.
Every 100 cycles (~17 min at 10 s; docs say ~50 min/hourly assuming 30 s): DataExpiryService (SQLite only), request-history purge (RequestHistoryRetentionDays), RetentionService.PruneAsync (Ask threads, jobs, import batches), FleetActionRunner.PruneExpiredRunsAsync. Each step is try/catch isolated so one failure does not skip the rest.
