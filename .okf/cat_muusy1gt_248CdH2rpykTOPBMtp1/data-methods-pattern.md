---
type: "Reference"
title: "Interface-per-entity data access: I<Entity>Methods x 4 providers"
description: "Each entity has an interface in Database/Interfaces (e.g. IVesselMethods) implemented once per provider in Database/<Provider>/Implementations/<Entity>Methods.cs with hand-written parameterized SQL; DatabaseDriver exposes them as properties (Database.Vessels, Database.Missions...)."
timestamp: "2026-10-05T05:45:03.5429400Z"
created: "2026-10-05T05:45:02.7331030Z"
slug: "data-methods-pattern"
category: "cat_muusy1gt_248CdH2rpykTOPBMtp1"
links: ["data-four-providers", "data-schema-migration-numbering", "data-json-and-typed-columns"]
version: "1"
salience: "0.5"
---
Layout under src/Armada.Core/Database: Interfaces/I*Methods.cs (about 50: Fleet, Vessel, Captain, Mission, Voyage, Dock, Signal, Event, MergeEntry, Tenant, User, Credential, PromptTemplate, Persona, Pipeline, Playbook, WorkflowProfile, ProjectProfile, Skill, DeploymentEnvironment, CheckRun, Release, Deployment, Objective(+Refinement Session/Message), PlanningSession(+Message), Job, RequestHistory, TokenUsage, CoordinationLease, ModelEndpoint, Harbor, Memory, VesselImport Batch/Item/FleetRecommendation, Ask Thread/Message/MessageToolCall/ActionProposal/TrackedWork, FleetAction/Run/RunTarget, VesselHealth/Finding/Dependency/Override); Sqlite|Postgresql|Mysql|SqlServer/Implementations/<Entity>Methods.cs; <Provider>/Queries/TableQueries.cs (DDL, indexes, migration statements); <Provider>/<Provider>DatabaseDriver.cs.

Method shape (IVesselMethods example): CreateAsync, ReadAsync(id), ReadByNameAsync, UpdateAsync, DeleteAsync, EnumerateAsync(), EnumerateAsync(EnumerationQuery) -> EnumerationResult<T>, ExistsAsync, plus tenant-scoped overloads (tenantId, id) and user-scoped overloads (tenantId, userId, id) used for Category A ownership scoping; all async with CancellationToken token = default and ConfigureAwait(false). SQL is hand-written with parameters (no ORM); readers map columns manually (e.g. NullableString, FromIso8601). Adding a column means: model property, DDL in all four TableQueries, a new migration on all four providers, every reader/writer, and parity tests. Incidents/runbook executions are event-backed rather than having their own tables (ObjectivePersistenceHelper normalizes objectives).
