---
type: "Reference"
title: "Incident (inc_): operational incident, also used by autonomous recovery"
description: "Incident (inc_) records impact, root cause, rollback and postmortem linked to deployments/releases/missions; statuses Open, Monitoring, Mitigated, RolledBack, Closed; MissionRecoveryCoordinator opens incidents for failed missions."
timestamp: "2026-10-05T05:32:24.4338400Z"
created: "2026-10-05T05:32:24.2130730Z"
slug: "term-incident"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["arch-autonomous-mission-recovery", "term-deployment-environment"]
version: "1"
salience: "0.5"
---
Term: Incident (src/Armada.Core/Models/Incident.cs, prefix inc_; IncidentService). Event-backed operational incident tied to environments, deployments, releases, vessels, missions, voyages. Status IncidentStatusEnum: Open, Monitoring, Mitigated, RolledBack, Closed. Severity IncidentSeverityEnum: Critical, High, Medium, Low. Records impact, root cause, recovery notes, rollback linkage, postmortem, hotfix handoff to planning/dispatch.

Second use: autonomous mission recovery. MissionRecoveryCoordinator opens a recovery incident (MissionId, RescueMissionIds, RecoveryAttempts, RecoveryNotes) for a recently failed recoverable mission, dispatches bounded rescue missions, and advances Open -> Mitigated (a rescue completed) -> Closed (next cycle). See [[arch-autonomous-mission-recovery]]. REST /api/v1/incidents; WebSocket incident.changed.
