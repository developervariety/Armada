# Additional foundation entity source census

The initial source review used foundation checkpoint 77353d49f55edd9264c02bc2dee1ab0945d14ddb. Later storage repairs are identified below; this remains a selected field census.

Scope: selected field/schema and SQL/mapper paths, not every line, runtime certification, SQL type certification, or service authorization proof. P means a source path is present and is a candidate for behavioral proof. Query means full-entity read/enumeration, not every filter. Current additive server prerequisite declarations are included in schema presence.

| Entity field group | Providers | Schema | Create | Update | Read | Query |
| --- | --- | --- | --- | --- | --- | --- |
| Objective selected fields | all four | P | P | P | P | P |
| Captain selected fields | all four | P | P | P | P | P |
| Vessel selected fields | all four | P | P | P | P | P |
| Voyage selected fields | all four | P | P | P | P | P |
| CheckRun selected fields | all four | P | P | P | P | P |
| Deployment selected fields | all four | P | P | P | P | P |
| MergeEntry selected fields | all four | P | P | P | P | P |
| LandingJob selected fields | all four | P | P | P | P | P |
| Captain.Tier | all four | additive | proved | proved, including NULL | proved after reopen | P |
| Captain.LastProcessAliveUtc | all four | P | omitted | dedicated UpdateProcessAliveAsync | P | P |
| Vessel.SecretScanEnabled, ProtectedPathPatterns, PrivateIdentifierDenylist | all four | preserved | proved | proved | proved after reopen | P |
| Vessel.RequirePassingChecksToLand, ProtectedBranchPatterns, ReleaseBranchPrefix, HotfixBranchPrefix, RequirePullRequestForProtectedBranches, RequireMergeQueueForReleaseBranches | all four | P, additive | proved | proved | proved after reopen | P |
| Voyage.SourcePlanningSessionId, SourcePlanningMessageId | SQLite | P | P | P | P | P |
| Voyage.SourcePlanningSessionId, SourcePlanningMessageId | PostgreSQL, MySQL, SQL Server | additive | proved | proved, including NULL | proved after reopen | P |
| CoordinationLease.Name, Holder, TenantId, AcquiredUtc, ExpiresUtc | SQLite, MySQL, SQL Server | P | TryAcquireAsync | TryRenewAsync | P | expiry purge |
| CoordinationLease.Name, Holder, TenantId, AcquiredUtc, ExpiresUtc | PostgreSQL | TIMESTAMPTZ time columns | TryAcquireAsync | TryRenewAsync | P | expiry purge |

## Selected field lists

- Objective: Preparation, AutoDispatchEnabled, StartFromRef, BlockedByObjectiveIds, SuggestedPipelineId.
- Captain: ApiKey, ApiBaseUrl, Model, RuntimeOptionsJson, AllowedPersonas, PreferredPersona, QuarantineUntilUtc, QuarantineReason, CurrentMissionId, CurrentDockId, ProcessId, RecoveryAttempts.
- Vessel: ArchitectMaxMissionsPerVoyage, AllowConcurrentMissions, SiblingRepos, DefaultPipelineId, ProtectedPaths, AutoLandPredicate, AutoLandCalibrationLandedCount, LandingMode, BranchCleanupPolicy.
- Voyage: CaptainOverridesJson, AutoPush, AutoCreatePullRequests, AutoMergePullRequests, LandingMode.
- CheckRun: Source, Command, CommitHash, BranchName, Type, Status, ExitCode, Output, TestSummary, CoverageSummary, Artifacts, DeploymentId.
- Deployment: ApprovalRequired, ApprovedByUserId, ApprovedUtc, ApprovalComment, VerificationStatus, CheckRunIds, DeployCheckRunId, RollbackCheckRunId, MonitoringWindowEndsUtc, LastMonitoredUtc, LastRegressionAlertUtc, LatestMonitoringSummary, MonitoringFailureCount.
- MergeEntry: AuditLane, AuditConventionPassed, AuditConventionNotes, AuditCriticalTrigger, AuditDeepPicked, AuditDeepCompletedUtc, AuditDeepVerdict, AuditDeepNotes, AuditDeepRecommendedAction, PrUrl, PrBaseBranch, MergeFailureClass, ConflictedFiles, MergeFailureSummary, DiffLineCount.
- LandingJob: MergeEntryId, MissionId, VesselId, BranchName, TargetBranch, State, RetryCount, LastError.

## Concrete findings and bounds

1. PostgreSQL CoordinationLease uses TIMESTAMPTZ in preserved migration 70. The registered lifecycle test passes acquire, read/reopen, rejected rival, owner renew, wrong-owner release, expiry takeover and purge. An initial source review incorrectly reported TEXT; the actual declaration and native catalog reject that finding.
2. Captain.Tier now has nullable storage on all four providers. A non-default create failed before repair; create, update and clearing now survive reopen. This preserves tier metadata and NULL inference; it does not replace configured routing selection.
3. The three Vessel scanner fields had schema but lost non-default values on reload on all four providers. Create/update/read mappings now preserve them. PostgreSQL retains its historical INTEGER boolean representation. Enforcement still uses global scanner settings and ProtectedPaths; stored preferences do not activate new policy.
4. The six Vessel preview fields failed non-default create/reopen checks on all four providers before repair. Additive migrations SQLite83, PostgreSQL84, MySQL75 and SQL Server78 now preserve create/update values across reopen. Populated upgrades, incompatible columns and interrupted restart have dedicated fixtures. Consumers are LandingPreviewService and VesselReadinessService; persistence does not add enforcement to immutable Check or landing gates. See [test discovery](test-discovery.md).
5. Captain.LastProcessAliveUtc is maintained by UpdateProcessAliveAsync, independently from the output heartbeat. Do not fix this by blindly adding it to general UpdateAsync: a stale captain object could erase a newer liveness observation. Test the dedicated update, read/reopen, and unchanged LastHeartbeatUtc.
6. Server Voyage planning-source fields now persist through new nullable columns. Their non-default create/reopen cases failed before repair; update and clearing are also verified. Server PlanningSession methods still explicitly throw NotSupportedException. Provenance storage does not enable that service.
7. CheckRun persistence UpdateAsync rewrites Source, Command, CommitHash and result fields. The immutable Check gate therefore needs service/API-level denial tests; a database round-trip only proves storage.
8. Voyage has no PipelineId model field. Pipeline stage membership is represented elsewhere. Do not invent a Voyage pipeline-column gate.

## Source anchors

For each provider P, writes/queries are in src/Armada.Core/Database/P/Implementations/EntityMethods.cs. SQLite and SQL Server base entity row mappers also live in PDatabaseDriver.cs. PostgreSQL and MySQL generally map base entities in their implementation files. Source candidates above include these mapper paths.

- PostgreSQL CoordinationLeaseMethods.cs: acquisition SQL around 65, renewal around 108, purge around 175, row casts around 193.
- SQLite CaptainMethods.cs: UpdateProcessAliveAsync around 330; equivalent method exists in all four providers.
- ObjectiveMethods.cs: AddParameters and FromReader carry preparation JSON, auto-dispatch and start reference together on all four providers.

See [backend storage evidence](backend-storage.md) for the eight field cases and additive migration scope.
