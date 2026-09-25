#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Runtime.CompilerServices;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>
    /// Runs integration tests against a live database driver.
    /// </summary>
    public class DatabaseTestRunner
    {
        private sealed class OperationalGraphResult
        {
            public TenantMetadata Tenant { get; set; } = null!;

            public UserMaster User { get; set; } = null!;

            public Credential Credential { get; set; } = null!;

            public Fleet Fleet { get; set; } = null!;

            public Vessel Vessel { get; set; } = null!;

            public Captain Captain { get; set; } = null!;

            public Voyage Voyage { get; set; } = null!;

            public Mission Mission { get; set; } = null!;

            public Dock Dock { get; set; } = null!;

            public Signal Signal { get; set; } = null!;

            public ArmadaEvent Event { get; set; } = null!;

            public MergeEntry MergeEntry { get; set; } = null!;
        }

        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;
        private List<TestResult> _Results = new List<TestResult>();

        public DatabaseTestRunner(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup = false)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        public async Task<List<TestResult>> RunAllAsync(CancellationToken token = default)
        {
            _Results = new List<TestResult>();

            Console.WriteLine("--- Schema Verification ---");
            await RunTest("Schema_Verify_Core_Columns_And_Indexes", "Schema", () => new SchemaVerificationTests(_Settings).VerifyAsync(token), token);

            Console.WriteLine();
            await RunTest("Schema_Repeat_Startup_Preserves_Version", "Schema", async () =>
            {
                int version = await _Driver.GetSchemaVersionAsync(token).ConfigureAwait(false);
                await _Driver.InitializeAsync(token).ConfigureAwait(false);
                DatabaseAssert.Equal(version, await _Driver.GetSchemaVersionAsync(token).ConfigureAwait(false), "Repeated startup schema version");
            }, token);
            await RunTest("Schema_Upgrade_Drops_Jobs_Table_Holding_Rows", "Schema", () => new SchemaVerificationTests(_Settings).VerifyJobsTableDroppedOnUpgradeAsync(token), token);

            await RunTest("ModelEndpoint_Persistence_Scope_Unicode_Reopen", "Operational", () => TestModelEndpointPersistenceAsync(token), token);
            await RunTest("Captain_ModelEndpoint_Link_Persists_Across_Reopen", "Operational", () => TestCaptainModelEndpointLinkAsync(token), token);
            await RunTest("ModelEndpoint_Health_Conditional_Update_CAS_And_Nulls", "Operational", () => TestModelEndpointHealthCasAsync(token), token);
            await RunTest("ModelEndpoint_Persistence_Rejects_Corrupt_Enums", "Operational", () => TestModelEndpointCorruptEnumsAsync(token), token);

            if (_Settings.Type == Armada.Core.Enums.DatabaseTypeEnum.Mysql)
                await RunTest("MySQL_Unicode_Full_Uniqueness_Concurrency_Rollback", "Schema", () => new MysqlUnicodeUniquenessTests(_Settings).VerifyAsync(token), token);

            await RunTest("CoordinationLease_Reopen_Ownership_Expiry", "Operational", () => TestCoordinationLeaseAsync(token), token);
            CoordinationDatabaseTests coordination = new CoordinationDatabaseTests(_Driver, _Settings, _NoCleanup);
            await RunTest("CoordinationRoom_Create_Read_Update_Reopen_Delete", "Operational", () => coordination.VerifyRoomsAsync(token), token);
            await RunTest("CoordinationParticipant_Upsert_Heartbeat_Race_Prune", "Operational", () => coordination.VerifyParticipantsAsync(token), token);
            await RunTest("CoordinationMessage_Create_Read_Update_Visibility_Delete", "Operational", () => coordination.VerifyMessagesAsync(token), token);
            await RunTest("CoordinationClaim_Create_Extend_Release_Reopen", "Operational", () => coordination.VerifyClaimsAsync(token), token);
            await RunTest("Backup_Native_Verified_Archive_Provider_Manifest_And_Restore_Contract", "Operational", () => TestNativeBackupAsync(token), token);
            await RunTest("HarborRunnerEnrollment_Reopen_And_CAS_Race", "Operational", () => new HarborRunnerEnrollmentDatabaseTests(_Driver, _Settings).VerifyAsync(token), token);
            await RunTest("HarborJob_RevisionGuard_RestartReconciliation_And_Reopen", "Operational", () => new HarborJobDatabaseTests(_Driver, _Settings).VerifyAsync(token), token);
            await RunTest("Objective_Terminal_Backlog_Migration_Repairs_Only_Terminal_Rows", "Operational", () => TestObjectiveTerminalBacklogMigrationAsync(token), token);
            await RunTest("MissionAttemptFacts_Window_Scope_Bound_Reopen", "Operational", () => new ProductionFactDatabaseTests(_Driver, _Settings).VerifyMissionAttemptFactsAsync(token), token);
            await RunTest("CheckRun_Regression_Links_Create_Update_Reopen", "Operational", () => new ProductionFactDatabaseTests(_Driver, _Settings).VerifyCheckRegressionLinksAsync(token), token);
            await RunTest("MemoryProposals_Create_List_Dismiss_Reopen", "Operational", () => new MemoryProposalDatabaseTests(_Driver, _Settings).VerifyAsync(token), token);
            await RunTest("PreparationClaimObservations_Window_Scope_Bound_Reopen", "Operational", () => new ProductionFactDatabaseTests(_Driver, _Settings).VerifyPreparationClaimObservationsAsync(token), token);
            await RunTest("LaneStateTransitions_And_CheckSlotRequest_Reopen", "Operational", () => new ProductionFactDatabaseTests(_Driver, _Settings).VerifyLaneStateAndSlotRequestAsync(token), token);
            await RunTest("TerminalVoyage_Mission_Reconciliation_Persists_And_Is_Idempotent", "Operational", () => new TerminalVoyageReconciliationDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("TerminalVoyage_Reconciled_Marker_Backfill_Matches_Reason_Rule", "Operational", () => new TerminalVoyageReconciledMarkerBackfillTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("Vessel_GitHubTokenOverride_Create_Keep_Replace_Clear_Reopen", "Operational", () => new VesselGitHubTokenOverrideDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("Mission_OperatorReviewHold_Create_Keep_Clear_Reopen", "Operational", () => new MissionOperatorHoldDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("Process_LaunchIdentity_Create_Update_Legacy_Reopen", "Operational", () => new ProcessLaunchIdentityDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("TokenUsage_InputBuckets_Create_Legacy_Reopen", "Operational", () => new TokenUsageInputBucketsDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("TokenUsage_Counts_Above_32_Bit_Round_Trip", "Operational", () => new TokenUsageInputBucketsDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyWideCountsAsync(token), token);
            await RunTest("Captain_PreferenceRank_And_Persona_Specialist_Create_Update_Reopen", "Operational", () => new TierRoutingDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);

            Console.WriteLine("--- Tenant/User/Credential ---");
            await RunTest("Tenant_Create_Read_Update_Enumerate", "Auth", () => TestTenantCrudAsync(token), token);
            await RunTest("Tenant_ReadByName_Exists", "Auth", () => TestTenantLookupAsync(token), token);
            await RunTest("User_Create_Read_Update_Enumerate", "Auth", () => TestUserCrudAsync(token), token);
            await RunTest("User_ReadByEmail_Exists", "Auth", () => TestUserLookupAsync(token), token);
            await RunTest("Credential_Create_Read_Update_Enumerate", "Auth", () => TestCredentialCrudAsync(token), token);
            await RunTest("Credential_ReadByBearerToken_EnumerateByUser", "Auth", () => TestCredentialLookupAsync(token), token);

            Console.WriteLine();
            Console.WriteLine("--- Operational Round-Trip ---");
            await RunTest("Fleet_Create_Read_Update_Enumerate", "Operational", () => TestFleetCrudAsync(token), token);
            await RunTest("Fleet_ReadByName_Exists_UnscopedEnumerate", "Operational", () => TestFleetLookupAsync(token), token);
            await RunTest("Vessel_Create_Read_Update_Enumerate", "Operational", () => TestVesselCrudAsync(token), token);
            foreach (string field in new[] { "RequirePassingChecksToLand", "ProtectedBranchPatterns", "ReleaseBranchPrefix", "HotfixBranchPrefix", "RequirePullRequestForProtectedBranches", "RequireMergeQueueForReleaseBranches" })
                await RunTest("Vessel_Preview_" + field + "_Create_Update_Reopen", "Operational", () => TestVesselPreviewFieldAsync(field, token), token);
            foreach (string field in new[] { "SecretScanEnabled", "ProtectedPathPatterns", "PrivateIdentifierDenylist" })
                await RunTest("Vessel_Scanner_" + field + "_Create_Update_Reopen", "Operational", () => TestVesselPreviewFieldAsync(field, token), token);
            await RunTest("Captain_Tier_Create_Update_Clear_Reopen", "Operational", () => TestBackendFieldAsync("Captain", "Tier", token), token);
            foreach (string field in new[] { "RequestedCaptainId", "Tier" })
                await RunTest("Mission_" + field + "_Create_Update_Clear_Reopen", "Operational", () => TestBackendFieldAsync("Mission", field, token), token);
            foreach (string field in new[] { "SourcePlanningSessionId", "SourcePlanningMessageId" })
                await RunTest("Voyage_" + field + "_Create_Update_Clear_Reopen", "Operational", () => TestBackendFieldAsync("Voyage", field, token), token);
            await RunTest("Captain_Create_Read_Update", "Operational", () => TestCaptainCrudAsync(token), token);
            await RunTest("Captain_Quarantine_Conditional_Hold_And_Release", "Operational", () => TestCaptainQuarantineConditionalAsync(token), token);
            await RunTest("MergeEntry_Scoped_Enumerate_Filters_By_Mission", "Operational", () => TestMergeEntryScopedMissionFilterAsync(token), token);
            await RunTest("Voyage_Create_Read_Update", "Operational", () => TestVoyageCrudAsync(token), token);
            await RunTest("Voyage_Summary_All_Pages_And_Scopes", "Operational", () => TestVoyageSummaryAsync(token), token);
            await RunTest("Mission_Admission_Long_Unicode_Ids_And_Reasons", "Operational", () => TestMissionAdmissionUnicodeAsync(token), token);
            await RunTest("Mission_Admission_Observation_Reopen_And_Stale_Write", "Operational", () => TestMissionAdmissionAsync(token), token);
            await RunTest("Dock_AnchorSnapshot_Create_Reopen", "Operational", () => TestDockAnchorSnapshotAsync(token), token);
            await RunTest("Mission_Create_Read_Update", "Operational", () => TestMissionCrudAsync(token), token);
            await RunTest("Mission_Summary_Reads_Skip_Heavy_Columns_And_Count_By_Voyage", "Operational", () => new MissionSummaryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("Mission_Enumerate_Applies_The_Same_Filters_At_Every_Scope", "Operational", () => new MissionSummaryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyEnumerationFiltersAsync(token), token);
            await RunTest("Mission_ActiveWorkFootprints_Count_Active_Work_Only", "Operational", () => TestActiveWorkFootprintsAsync(token), token);
            await RunTest("Mission_Fork_Fields_Create_Update_Reopen_Query", "Operational", () => TestMissionForkFieldsAsync(token), token);
            await RunTest("Dock_Create_Read_Update", "Operational", () => TestDockCrudAsync(token), token);
            await RunTest("Signal_Create_Read_Enumerate_MarkRead", "Operational", () => TestSignalCrudAsync(token), token);
            await RunTest("Signal_EnumerateRecent_Recipient_Unread", "Operational", () => TestSignalLookupAsync(token), token);
            await RunTest("Event_Create_Read_Enumerate", "Operational", () => TestEventCrudAsync(token), token);
            await RunTest("Event_FilteredEnumerations", "Operational", () => TestEventLookupAsync(token), token);
            await RunTest("MergeEntry_Create_Read_Update_Enumerate", "Operational", () => TestMergeEntryCrudAsync(token), token);
            await RunTest("MergeEntry_EnumerateByStatus_Exists", "Operational", () => TestMergeEntryLookupAsync(token), token);
            await RunTest("WorkflowProfile_Create_Read_Update_Enumerate", "Operational", () => TestWorkflowProfileCrudAsync(token), token);
            OperationalRoundTripDatabaseTests roundTrips = new OperationalRoundTripDatabaseTests(_Driver, _Settings, _NoCleanup);
            await RunTest("Skill_Create_Read_Update_Reopen_Window_Delete", "Operational", () => roundTrips.VerifySkillsAsync(token), token);
            await RunTest("ProjectProfile_Create_Read_Update_Reopen_Window_Delete", "Operational", () => roundTrips.VerifyProjectProfilesAsync(token), token);
            await RunTest("ProjectProfile_Damaged_Json_Is_Named_Not_Read_As_Empty", "Operational", () => roundTrips.VerifyDamagedJsonIsNamedAsync(token), token);
            await RunTest("Playbook_Create_Read_Empty_Text_Reads_As_Null_And_Snapshot_Reopen", "Operational", () => roundTrips.VerifyPlaybookEmptyTextAsync(token), token);
            await RunTest("LandingJob_Create_Read_Update_Reopen_State_Delete", "Operational", () => roundTrips.VerifyLandingJobsAsync(token), token);
            await RunTest("JudgeFollowUp_Upsert_Associate_Audit_Update_Reopen", "Operational", () => roundTrips.VerifyJudgeFollowUpsAsync(token), token);
            await RunTest("Tenant_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyTenantsAsync(token), token);
            await RunTest("User_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyUsersAsync(token), token);
            await RunTest("Credential_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyCredentialsAsync(token), token);
            await RunTest("Fleet_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyFleetsAsync(token), token);
            await RunTest("Signal_Every_Property_Create_MarkRead_Reopen", "Operational", () => roundTrips.VerifySignalsAsync(token), token);
            await RunTest("Event_Every_Property_Create_Reopen", "Operational", () => roundTrips.VerifyEventsAsync(token), token);
            await RunTest("Captain_Every_Property_Create_Liveness_Update_Reopen", "Operational", () => roundTrips.VerifyCaptainsAsync(token), token);
            await RunTest("WorkflowProfile_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyWorkflowProfilesAsync(token), token);
            await RunTest("CheckRun_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyCheckRunsAsync(token), token);
            await RunTest("DeploymentEnvironment_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyDeploymentEnvironmentsAsync(token), token);
            await RunTest("Release_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyReleasesAsync(token), token);
            await RunTest("Deployment_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyDeploymentsAsync(token), token);
            await RunTest("Delivery_And_Endpoint_Damaged_Json_Is_Named_Not_Read_As_Empty", "Operational", () => roundTrips.VerifyDamagedDeliveryJsonIsNamedAsync(token), token);
            await RunTest("Memory_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyMemoriesAsync(token), token);
            await RunTest("ModelEndpoint_Every_Property_Create_Health_Update_Reopen", "Operational", () => roundTrips.VerifyModelEndpointsAsync(token), token);
            await RunTest("PromptTemplate_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyPromptTemplatesAsync(token), token);
            await RunTest("TokenUsage_Every_Property_Full_And_Sparse_Reopen", "Operational", () => roundTrips.VerifyTokenUsageRecordsAsync(token), token);
            await RunTest("RequestHistory_Every_Property_Entry_And_Detail_Reopen", "Operational", () => roundTrips.VerifyRequestHistoryAsync(token), token);
            await RunTest("Persona_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyPersonasAsync(token), token);
            await RunTest("Pipeline_Every_Property_With_Stages_Create_Update_Reopen", "Operational", () => roundTrips.VerifyPipelinesAsync(token), token);
            await RunTest("Dock_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyDocksAsync(token), token);
            await RunTest("Voyage_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyVoyagesAsync(token), token);
            await RunTest("Mission_Every_Property_Create_Update_Reopen", "Operational", () => roundTrips.VerifyMissionsAsync(token), token);
            await RunTest("Pipeline_Update_And_Delete_Roll_Back_On_Failure", "Operational", () => TestPipelineWriteAtomicityAsync(token), token);
            await RunTest("Pipeline_Same_Order_Stages_Keep_Submitted_Order", "Operational", () => TestPipelineSiblingOrderAsync(token), token);
            await RunTest("RequestHistory_Timestamp_RoundTrip_And_Same_Day_Range", "Operational", () => TestRequestHistorySameDayRangeAsync(token), token);
            await RunTest("CheckRun_Create_Read_Update_Enumerate", "Operational", () => TestCheckRunCrudAsync(token), token);
            await RunTest("Environment_Create_Read_Update_Enumerate", "Operational", () => TestEnvironmentCrudAsync(token), token);
            await RunTest("Release_Create_Read_Update_Enumerate", "Operational", () => TestReleaseCrudAsync(token), token);
            await RunTest("Deployment_Create_Read_Update_Enumerate", "Operational", () => TestDeploymentCrudAsync(token), token);
            await RunTest("Objective_Create_Read_Update_Enumerate", "Operational", () => TestObjectiveCrudAsync(token), token);
            await RunTest("Objective_Unreadable_Stored_Data_Is_Named_And_Skipped_From_Lists", "Operational", () => TestObjectiveUnreadableStoredDataAsync(token), token);
            await RunTest("ObjectiveRefinementSession_Message_Create_Read_Update_Enumerate", "Operational", () => TestObjectiveRefinementCrudAsync(token), token);
            await RunTest("ObjectiveRefinementSession_Unknown_Stored_Status_Is_Named_And_Skipped_From_Lists", "Operational", () => TestObjectiveRefinementUnreadableStatusAsync(token), token);
            await RunTest("PlanningSession_Message_Crud_Scope_Order_And_Cascade", "Operational", () => TestPlanningSessionCrudAsync(token), token);
            await RunTest("Memory_Create_Read_Update_Tags_Reopen", "Operational", () => TestMemoryCrudAsync(token), token);
            await RunTest("Memory_Tenant_Fence_Key_Uniqueness_Guarded_Update", "Operational", () => TestMemoryScopingAsync(token), token);
            await RunTest("Configuration_Ownership_Create_Update_Reopen", "Operational", () => TestConfigurationOwnershipAsync(token), token);

            Console.WriteLine();
            Console.WriteLine("--- Cascade Verification ---");
            await RunTest("Objective_ForeignKeys_And_Refinement_Cascade", "Cascade", () => TestObjectiveForeignKeysAsync(token), token);
            await RunTest("Captain_Delete_Clears_Signal_References_Only_When_The_Captain_Is_Removed", "Cascade", () => new CaptainDeleteDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyAsync(token), token);
            await RunTest("Tenant_Delete_Cascades_Auth_Data", "Cascade", () => TestTenantAuthCascadeDeleteAsync(token), token);
            await RunTest("Tenant_Delete_With_Operational_Subordinates_Is_FK_Fenced", "Cascade", () => TestTenantDeleteFencedByOperationalDataAsync(token), token);

            MultiTenantScopingTests scopingTests = new MultiTenantScopingTests(_Driver, _NoCleanup);
            List<TestResult> scopingResults = await scopingTests.RunAllAsync(token).ConfigureAwait(false);
            _Results.AddRange(scopingResults);

            Console.WriteLine();
            Console.WriteLine("--- Data Expiry ---");
            await RunTest("DataExpiry_Purges_Expired_Rows_And_Keeps_Retained_Rows", "Retention", () => new DataExpiryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyRetentionPurgeAsync(token), token);
            await RunTest("DataExpiry_Keeps_Latest_Incident_Snapshots_Tombstones_And_Reversals", "Retention", () => new DataExpiryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyDurableEventsSurviveRetentionAsync(token), token);
            await RunTest("DataExpiry_Keeps_Latest_Runbook_Execution_Snapshot", "Retention", () => new DataExpiryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyLatestRunbookExecutionSnapshotSurvivesRetentionAsync(token), token);
            await RunTest("DataExpiry_Purges_Production_Facts_Older_Than_Fact_Retention", "Retention", () => new DataExpiryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyProductionFactRetentionAsync(token), token);
            await RunTest("DataExpiry_Purges_Request_History_Older_Than_Request_Retention", "Retention", () => new DataExpiryDatabaseTests(_Driver, _Settings, _NoCleanup).VerifyRequestHistoryRetentionAsync(token), token);

            return _Results;
        }

        private async Task TestModelEndpointPersistenceAsync(CancellationToken token)
        {
            string longId = "mep_" + new string('界', 446);
            string secondLongId = "mep_" + new string('界', 445) + "甲";
            ModelEndpoint endpoint = new ModelEndpoint
            {
                Id = longId,
                TenantId = "tenant-ユニコード",
                UserId = "user-ユニコード",
                Name = "Endpoint 名前",
                Kind = ModelEndpointKindEnum.Embedding,
                Scope = ScopeEnum.UserSpecific,
                Provider = ModelProviderEnum.OpenAICompatible,
                BaseUrl = "http://localhost:9999/v1",
                Model = "モデル",
                Dimensionality = 1536,
                TimeoutMs = 5000,
                Enabled = false,
                HealthStatus = EndpointHealthStatusEnum.Unhealthy,
                LastHealthError = "failure",
                LastLatencyMs = 42,
                HealthHistory = new List<ModelEndpointHealthRecord> { new ModelEndpointHealthRecord { Success = true } }
            };
            ModelEndpoint created = await _Driver.ModelEndpoints.CreateAsync(endpoint, token).ConfigureAwait(false);
            ModelEndpoint stored = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(longId, token).ConfigureAwait(false), "Model endpoint survives create");
            DatabaseAssert.Equal(ScopeEnum.UserSpecific, stored.Scope, "Scope round trip");
            DatabaseAssert.Equal("モデル", stored.Model, "Unicode model round trip");
            DatabaseAssert.Equal(ModelEndpointKindEnum.Embedding, stored.Kind, "Kind round trip");
            DatabaseAssert.Equal(ModelProviderEnum.OpenAICompatible, stored.Provider, "Provider round trip");
            DatabaseAssert.Equal("http://localhost:9999/v1", stored.BaseUrl, "Base URL round trip");
            DatabaseAssert.Equal(1536, stored.Dimensionality, "Dimensionality round trip");
            DatabaseAssert.Equal(5000, stored.TimeoutMs, "Timeout round trip");
            DatabaseAssert.True(!stored.Enabled, "Enabled round trip");
            DatabaseAssert.Equal(EndpointHealthStatusEnum.Unhealthy, stored.HealthStatus, "Health status round trip");
            DatabaseAssert.Equal("failure", stored.LastHealthError, "Health error round trip");
            DatabaseAssert.Equal(42L, stored.LastLatencyMs, "Latency round trip");
            DatabaseAssert.Equal(1, stored.HealthHistory.Count, "Health history round trip");
            stored.Scope = ScopeEnum.TenantWide;
            stored.LastHealthError = null;
            stored.HealthHistory = new List<ModelEndpointHealthRecord>();
            await _Driver.ModelEndpoints.UpdateAsync(stored, token).ConfigureAwait(false);
            using (DatabaseDriver reopenedDriver = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                ModelEndpoint reopened = DatabaseAssert.NotNull(await reopenedDriver.ModelEndpoints.ReadAsync(longId, token).ConfigureAwait(false), "Model endpoint survives reopen");
            DatabaseAssert.Equal(ScopeEnum.TenantWide, reopened.Scope, "Updated scope survives reopen");
            DatabaseAssert.True(reopened.LastHealthError == null, "Nullable health error clears");
            DatabaseAssert.Equal(0, reopened.HealthHistory.Count, "Health history clears");
            DatabaseAssert.UtcInstant(created.CreatedUtc, reopened.CreatedUtc, "Reopened ModelEndpoint.CreatedUtc");
                ModelEndpoint other = new ModelEndpoint { Id = secondLongId, TenantId = endpoint.TenantId, UserId = "other-user", Name = "Other", Scope = ScopeEnum.UserSpecific, BaseUrl = "http://localhost:9998" };
                await reopenedDriver.ModelEndpoints.CreateAsync(other, token).ConfigureAwait(false);
                ModelEndpoint otherStored = DatabaseAssert.NotNull(await reopenedDriver.ModelEndpoints.ReadAsync(other.Id, token).ConfigureAwait(false), "Second model endpoint survives create");
                DatabaseAssert.True(!otherStored.Enabled, "New model endpoints default to disabled");
                DatabaseAssert.Equal(2, (await reopenedDriver.ModelEndpoints.EnumerateAsync(endpoint.TenantId!, token).ConfigureAwait(false)).Count, "Tenant enumeration includes both records");
                DatabaseAssert.Equal(0, (await reopenedDriver.ModelEndpoints.EnumerateAsync("other-tenant", token).ConfigureAwait(false)).Count, "Other tenant cannot enumerate records");
                DatabaseAssert.True(await reopenedDriver.ModelEndpoints.ReadAsync("other-tenant", longId, token).ConfigureAwait(false) == null, "Other tenant cannot read record");
                DatabaseAssert.True(await reopenedDriver.ModelEndpoints.ReadAsync(endpoint.TenantId!, "other-user", longId, token).ConfigureAwait(false) == null, "Other user cannot read user-specific record");
                await reopenedDriver.ModelEndpoints.DeleteAsync(longId, token).ConfigureAwait(false);
                await reopenedDriver.ModelEndpoints.DeleteAsync(other.Id, token).ConfigureAwait(false);
            }
        }

        private async Task TestModelEndpointCorruptEnumsAsync(CancellationToken token)
        {
            string id = "mep_corrupt_enum_" + Guid.NewGuid().ToString("N");
            ModelEndpoint endpoint = new ModelEndpoint
            {
                Id = id,
                TenantId = "tenant-corrupt-enum",
                UserId = "user-corrupt-enum",
                Name = "Corrupt enum fixture",
                Kind = ModelEndpointKindEnum.Inference,
                Scope = ScopeEnum.TenantWide,
                Provider = ModelProviderEnum.OpenAI,
                BaseUrl = "http://localhost:9999",
                Enabled = false
            };
            await _Driver.ModelEndpoints.CreateAsync(endpoint, token).ConfigureAwait(false);

            foreach (string field in new[] { "kind", "scope", "provider" })
            {
                await UpdateRawEndpointFieldAsync(id, field, "Corrupt", token).ConfigureAwait(false);
                bool rejected = false;
                try
                {
                    await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false);
                }
                catch (StoredRowException ex) when (ex.Entity == "ModelEndpoint" && ex.Column == field)
                {
                    rejected = true;
                }

                DatabaseAssert.True(rejected, "Persisted invalid " + field + " must be rejected");
                string valid = field switch
                {
                    "kind" => ModelEndpointKindEnum.Inference.ToString(),
                    "scope" => ScopeEnum.TenantWide.ToString(),
                    "provider" => ModelProviderEnum.OpenAI.ToString(),
                    _ => throw new InvalidOperationException("Unexpected enum field")
                };
                await UpdateRawEndpointFieldAsync(id, field, valid, token).ConfigureAwait(false);
            }

            await _Driver.ModelEndpoints.DeleteAsync(id, token).ConfigureAwait(false);
        }

        private async Task TestCaptainModelEndpointLinkAsync(CancellationToken token)
        {
            string endpointId = "mep_captain_link_" + Guid.NewGuid().ToString("N");
            ModelEndpoint endpoint = new ModelEndpoint
            {
                Id = endpointId,
                TenantId = null,
                UserId = null,
                Name = "Captain link endpoint",
                Kind = ModelEndpointKindEnum.Inference,
                Scope = ScopeEnum.TenantWide,
                Provider = ModelProviderEnum.OpenAICompatible,
                BaseUrl = "http://localhost:9999",
                Model = "link-model",
                Enabled = false
            };
            Captain captain = new Captain("Captain endpoint link", AgentRuntimeEnum.ApiEndpoint)
            {
                Id = "cpt_model_endpoint_link_" + Guid.NewGuid().ToString("N"),
                TenantId = null,
                UserId = null,
                ModelEndpointId = endpoint.Id
            };

            await _Driver.ModelEndpoints.CreateAsync(endpoint, token).ConfigureAwait(false);
            try
            {
                await _Driver.Captains.CreateAsync(captain, token).ConfigureAwait(false);
                try
                {
                    using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                    {
                        Captain stored = DatabaseAssert.NotNull(await reopened.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false), "Captain survives endpoint-link migration and reopen");
                        DatabaseAssert.Equal(endpoint.Id, stored.ModelEndpointId, "Captain model endpoint link round trip");
                        DatabaseAssert.Equal(AgentRuntimeEnum.ApiEndpoint, stored.Runtime, "API endpoint runtime round trip");
                    }
                }
                finally
                {
                    await _Driver.Captains.DeleteAsync(captain.Id, token).ConfigureAwait(false);
                }
            }
            finally
            {
                await _Driver.ModelEndpoints.DeleteAsync(endpoint.Id, token).ConfigureAwait(false);
            }
        }

        private async Task TestModelEndpointHealthCasAsync(CancellationToken token)
        {
            string id = "mep_health_cas_" + Guid.NewGuid().ToString("N");
            ModelEndpoint endpoint = new ModelEndpoint
            {
                Id = id,
                TenantId = "tenant-health-cas",
                UserId = "user-health-cas",
                Name = "Health CAS fixture",
                Kind = ModelEndpointKindEnum.Inference,
                Scope = ScopeEnum.TenantWide,
                Provider = ModelProviderEnum.OpenAI,
                BaseUrl = "http://localhost:9999",
                Model = "health-cas-model"
            };
            await _Driver.ModelEndpoints.CreateAsync(endpoint, token).ConfigureAwait(false);
            try
            {
                ModelEndpoint first = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Health CAS fixture survives create");
                DateTime expectedStoredTimestamp = new DateTime(2037, 4, 5, 6, 7, 8, DateTimeKind.Utc).AddTicks(9012340);
                first.LastUpdateUtc = expectedStoredTimestamp;
                await _Driver.ModelEndpoints.UpdateAsync(first, token).ConfigureAwait(false);
                first = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Health CAS fixture preserves subsecond timestamp");
                DatabaseAssert.Equal(expectedStoredTimestamp, first.LastUpdateUtc, "DateTime2 timestamp must preserve fractional seconds");
                DateTime expectedInitialVersion = first.LastUpdateUtc;
                first.HealthStatus = EndpointHealthStatusEnum.Healthy;
                first.LastHealthCheckUtc = null;
                first.LastHealthError = null;
                first.LastLatencyMs = null;
                first.HealthHistory = new List<ModelEndpointHealthRecord>();
                DatabaseAssert.True(await _Driver.ModelEndpoints.UpdateHealthAsync(first, expectedInitialVersion, token).ConfigureAwait(false), "Conditional health update should succeed for the expected generation");

                ModelEndpoint afterHealth = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Health CAS fixture survives health update");
                DatabaseAssert.Equal(EndpointHealthStatusEnum.Healthy, afterHealth.HealthStatus, "Health status update round trip");
                DatabaseAssert.True(afterHealth.LastHealthCheckUtc == null, "Nullable health timestamp must remain null");
                DatabaseAssert.True(afterHealth.LastHealthError == null, "Nullable health error must remain null");
                DatabaseAssert.True(afterHealth.LastLatencyMs == null, "Nullable latency must remain null");

                DateTime expectedAfterHealthVersion = afterHealth.LastUpdateUtc;
                ModelEndpoint staleHealth = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Stale health fixture captures the old row");
                ModelEndpoint changed = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Configuration edit reads a fresh row");
                changed.Name = "Configuration edit";
                changed.BaseUrl = "http://localhost:10001";
                changed.ApiKey = "new-health-key";
                changed.Model = "new-health-model";
                changed.LastUpdateUtc = expectedAfterHealthVersion.AddSeconds(1);
                await _Driver.ModelEndpoints.UpdateAsync(changed, token).ConfigureAwait(false);

                staleHealth.HealthStatus = EndpointHealthStatusEnum.Unhealthy;
                staleHealth.LastHealthCheckUtc = DateTime.UtcNow;
                staleHealth.LastHealthError = "stale result";
                staleHealth.LastLatencyMs = 99;
                DatabaseAssert.True(!await _Driver.ModelEndpoints.UpdateHealthAsync(staleHealth, expectedAfterHealthVersion, token).ConfigureAwait(false), "A stale observed generation must be rejected");
                ModelEndpoint final = DatabaseAssert.NotNull(await _Driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Health CAS fixture survives stale update");
                DatabaseAssert.Equal("Configuration edit", final.Name, "Rejected health update must preserve configuration name");
                DatabaseAssert.Equal("http://localhost:10001", final.BaseUrl, "Rejected health update must preserve configuration URL");
                DatabaseAssert.Equal("new-health-key", final.ApiKey, "Rejected health update must preserve configuration key");
                DatabaseAssert.Equal("new-health-model", final.Model, "Rejected health update must preserve configuration model");
                DatabaseAssert.Equal(EndpointHealthStatusEnum.Healthy, final.HealthStatus, "Rejected health update must preserve newer health");
            }
            finally
            {
                await _Driver.ModelEndpoints.DeleteAsync(id, token).ConfigureAwait(false);
            }
        }

        private async Task UpdateRawEndpointFieldAsync(string id, string field, string value, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE model_endpoints SET " + field + " = @value WHERE id = @id;";
                    DbParameter valueParameter = command.CreateParameter();
                    valueParameter.ParameterName = "@value";
                    valueParameter.Value = value;
                    command.Parameters.Add(valueParameter);
                    DbParameter idParameter = command.CreateParameter();
                    idParameter.ParameterName = "@id";
                    idParameter.Value = id;
                    command.Parameters.Add(idParameter);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private async Task RunTest(string name, string category, Func<Task> action, CancellationToken token, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        {
            TestResult result = new TestResult(name, category);
            result.SetSource(sourcePath, sourceLine);
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                token.ThrowIfCancellationRequested();
                await action().ConfigureAwait(false);
                sw.Stop();
                result.MarkPassed(sw.Elapsed);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [PASS] ");
                Console.ResetColor();
                Console.WriteLine(name + " (" + sw.ElapsedMilliseconds + "ms)");
            }
            catch (DatabaseTestSkipException skip)
            {
                sw.Stop();
                result.MarkSkipped(sw.Elapsed, skip.Message);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("  [SKIP] ");
                Console.ResetColor();
                Console.WriteLine(name + " - " + skip.Message);
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.MarkFailed(sw.Elapsed, ex.Message, ex);

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.ResetColor();
                Console.WriteLine(name + " (" + sw.ElapsedMilliseconds + "ms) - " + ex.Message);
            }

            _Results.Add(result);
        }

        private static IReadOnlyList<string> RequiredNativeClients(DatabaseTypeEnum type)
        {
            switch (type)
            {
                case DatabaseTypeEnum.Postgresql: return new[] { "pg_dump", "createdb", "pg_restore", "psql", "dropdb" };
                case DatabaseTypeEnum.Mysql: return new[] { "mysql", "mysqldump" };
                case DatabaseTypeEnum.SqlServer: return new[] { "sqlcmd" };
                default: return Array.Empty<string>();
            }
        }

        private static bool IsOnPath(string tool)
        {
            string[] names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool + ".cmd", tool } : new[] { tool };
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? String.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string name in names)
                {
                    if (File.Exists(Path.Combine(directory, name))) return true;
                }
            }
            return false;
        }

        private async Task TestNativeBackupAsync(CancellationToken token)
        {
            string root = Path.Combine(Path.GetTempPath(), "armada-dbtest-backup-" + Guid.NewGuid().ToString("N"));
            try
            {
                ArmadaSettings armada = new ArmadaSettings
                {
                    DataDirectory = Path.Combine(root, "data"),
                    Database = _Settings
                };
                // Native backup runs the provider's client tools. Check every prerequisite before doing any work so an
                // unprepared host reports a named skip instead of a failure from inside the backup.
                foreach (string tool in RequiredNativeClients(_Settings.Type))
                {
                    if (!IsOnPath(tool))
                        throw new DatabaseTestSkipException("native_client_missing_" + tool
                            + ": install the client or run scripts/common/install-database-client-wrappers.sh (see test/Armada.Test.Database/README.md)");
                }
                string? sqlServerDirectory = Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY");
                if (_Settings.Type == DatabaseTypeEnum.SqlServer && String.IsNullOrWhiteSpace(sqlServerDirectory))
                    throw new DatabaseTestSkipException("sqlserver_backup_directory_not_configured: set ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY to a directory visible to the SQL Server host");
                armada.SelfDeploy.SqlServerBackupDirectory = sqlServerDirectory;

                await _Driver.Fleets.CreateAsync(new Fleet("backup-proof-" + Guid.NewGuid().ToString("N")), token).ConfigureAwait(false);
                long expectedFleets = (await _Driver.Fleets.EnumerateAsync(token).ConfigureAwait(false)).Count;
                int expectedSchema = await _Driver.GetSchemaVersionAsync(token).ConfigureAwait(false);
                DatabaseBackupService backups = new DatabaseBackupService(_Driver, armada);
                string archive = Path.Combine(root, "backup.zip");

                DatabaseBackupResult result = await backups.BackupAsync(archive, token).ConfigureAwait(false);

                DatabaseAssert.Equal(_Settings.Type, result.DatabaseType, "Backup provider");
                DatabaseAssert.Equal(expectedSchema, result.SchemaVersion, "Backup schema version from the provider");
                DatabaseAssert.Equal(expectedFleets, result.RecordCounts["fleets"], "Backup fleet count from the provider");
                DatabaseBackupManifest manifest;
                using (ZipArchive zip = ZipFile.OpenRead(archive))
                {
                    ZipArchiveEntry manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("manifest.json missing");
                    JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    options.Converters.Add(new JsonStringEnumConverter());
                    using (Stream stream = manifestEntry.Open())
                    {
                        manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(stream, options) ?? throw new InvalidOperationException("manifest unreadable");
                    }
                    DatabaseAssert.Equal(_Settings.Type, manifest.DatabaseType, "Manifest provider");
                    DatabaseAssert.Equal(true, manifest.BackupValidated, "Native backup validated");
                    DatabaseAssert.Equal(true, manifest.RestoreVerified, "Isolated restore verified");
                    DatabaseAssert.Equal(expectedFleets, manifest.RecordCounts["fleets"], "Manifest fleet count");
                    if (String.IsNullOrEmpty(manifest.ServerArtifactPath))
                    {
                        ZipArchiveEntry artifact = zip.GetEntry(manifest.ArtifactEntry) ?? throw new InvalidOperationException("artifact entry missing: " + manifest.ArtifactEntry);
                        if (artifact.Length == 0) throw new InvalidOperationException("artifact entry is empty");
                        using (Stream stream = artifact.Open())
                        {
                            string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
                            DatabaseAssert.Equal(manifest.ArtifactSha256, digest, "Artifact digest");
                        }
                    }
                    else
                    {
                        DatabaseAssert.Equal(DatabaseTypeEnum.SqlServer, _Settings.Type, "Only SQL Server keeps the artifact on the database host");
                    }
                }

                if (_Settings.Type != DatabaseTypeEnum.Sqlite)
                {
                    string reason = String.Empty;
                    try
                    {
                        await backups.RestoreAsync(archive, null, token).ConfigureAwait(false);
                    }
                    catch (DatabaseBackupException ex)
                    {
                        reason = ex.FailureReason;
                    }
                    DatabaseAssert.Equal("restore_unsupported_for_provider_" + _Settings.Type, reason, "Server provider restore refused");
                    DatabaseAssert.Equal(expectedFleets, (long)(await _Driver.Fleets.EnumerateAsync(token).ConfigureAwait(false)).Count, "Refused restore changed nothing");
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (IOException ex)
                {
                    Console.WriteLine("  backup test cleanup left " + root + ": " + ex.Message);
                }
            }
        }

        private async Task TestCoordinationLeaseAsync(CancellationToken token)
        {
            string name = "database-lease-" + Guid.NewGuid().ToString("N");
            try
            {
                DatabaseAssert.True(await _Driver.CoordinationLeases.TryAcquireAsync(name, "first", TimeSpan.FromMinutes(1), token: token).ConfigureAwait(false), "Acquire lease");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    CoordinationLease lease = DatabaseAssert.NotNull(await reopened.CoordinationLeases.ReadAsync(name, token).ConfigureAwait(false), "Lease persists after reopen");
                    DatabaseAssert.Equal("first", lease.Holder, "Persistent lease holder");
                    DatabaseAssert.True(lease.ExpiresUtc > DateTime.UtcNow, "Persistent lease expiry");
                    DatabaseAssert.True(!await reopened.CoordinationLeases.TryAcquireAsync(name, "second", TimeSpan.FromMinutes(1), token: token).ConfigureAwait(false), "Live owner blocks takeover");
                    DatabaseAssert.True(!await reopened.CoordinationLeases.TryRenewAsync(name, "second", TimeSpan.FromMinutes(1), token).ConfigureAwait(false), "Wrong holder cannot renew");
                    await reopened.CoordinationLeases.ReleaseAsync(name, "second", token).ConfigureAwait(false);
                    DatabaseAssert.True(await reopened.CoordinationLeases.TryRenewAsync(name, "first", TimeSpan.FromMinutes(2), token).ConfigureAwait(false), "Wrong holder cannot release");
                    await reopened.CoordinationLeases.ReleaseAsync(name, "first", token).ConfigureAwait(false);
                    DatabaseAssert.True(await reopened.CoordinationLeases.ReadAsync(name, token).ConfigureAwait(false) == null, "Owner release removes lease");
                    DatabaseAssert.True(await reopened.CoordinationLeases.TryAcquireAsync(name, "expired", TimeSpan.FromSeconds(-1), token: token).ConfigureAwait(false), "Expired fixture");
                    DatabaseAssert.True(await reopened.CoordinationLeases.TryAcquireAsync(name, "replacement", TimeSpan.FromSeconds(-1), token: token).ConfigureAwait(false), "Expired owner permits takeover");
                    await reopened.CoordinationLeases.PurgeExpiredAsync(token).ConfigureAwait(false);
                    DatabaseAssert.True(await reopened.CoordinationLeases.ReadAsync(name, token).ConfigureAwait(false) == null, "Purge removes expired lease");
                }
            }
            finally
            {
                foreach (string holder in new[] { "first", "second", "expired", "replacement" })
                    await _Driver.CoordinationLeases.ReleaseAsync(name, holder, token).ConfigureAwait(false);
            }
        }

        private async Task TestObjectiveTerminalBacklogMigrationAsync(CancellationToken token)
        {
            string[] statements = TerminalBacklogMigrationStatements();
            DatabaseAssert.True(statements.Length > 0, "Terminal backlog migration is registered for " + _Settings.Type);

            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("terminal-backlog", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "terminal-backlog", token: token).ConfigureAwait(false);
                Dictionary<string, ObjectiveBacklogStateEnum> expected = new Dictionary<string, ObjectiveBacklogStateEnum>();
                Dictionary<string, DateTime> updatedBefore = new Dictionary<string, DateTime>();

                async Task SeedAsync(ObjectiveStatusEnum status, ObjectiveBacklogStateEnum state, ObjectiveBacklogStateEnum after)
                {
                    Objective objective = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "terminal-backlog", token: token).ConfigureAwait(false);
                    objective.Status = status;
                    objective.BacklogState = state;
                    await _Driver.Objectives.UpdateAsync(objective, token).ConfigureAwait(false);
                    Objective stored = DatabaseAssert.NotNull(await _Driver.Objectives.ReadAsync(objective.Id, token).ConfigureAwait(false), "Seeded objective");
                    DatabaseAssert.Equal(state, stored.BacklogState, "Seeded contradictory backlog state persists before migration");
                    expected[objective.Id] = after;
                    updatedBefore[objective.Id] = stored.LastUpdateUtc;
                }

                await SeedAsync(ObjectiveStatusEnum.Completed, ObjectiveBacklogStateEnum.ReadyForDispatch, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Completed, ObjectiveBacklogStateEnum.Dispatched, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Cancelled, ObjectiveBacklogStateEnum.Refining, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Completed, ObjectiveBacklogStateEnum.Inbox, ObjectiveBacklogStateEnum.Inbox).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Planned, ObjectiveBacklogStateEnum.ReadyForDispatch, ObjectiveBacklogStateEnum.ReadyForDispatch).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.InProgress, ObjectiveBacklogStateEnum.Dispatched, ObjectiveBacklogStateEnum.Dispatched).ConfigureAwait(false);
                await SeedAsync(ObjectiveStatusEnum.Deployed, ObjectiveBacklogStateEnum.ReadyForDispatch, ObjectiveBacklogStateEnum.ReadyForDispatch).ConfigureAwait(false);

                using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    foreach (string statement in statements)
                    {
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = statement;
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                }

                foreach (KeyValuePair<string, ObjectiveBacklogStateEnum> row in expected)
                {
                    Objective stored = DatabaseAssert.NotNull(await _Driver.Objectives.ReadAsync(row.Key, token).ConfigureAwait(false), "Migrated objective");
                    DatabaseAssert.Equal(row.Value, stored.BacklogState, "Backlog state after migration for " + stored.Status);
                    DatabaseAssert.Equal(updatedBefore[row.Key], stored.LastUpdateUtc, "Migration keeps the update time for " + stored.Status);
                }
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private string[] TerminalBacklogMigrationStatements()
        {
            int version = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => 92,
                DatabaseTypeEnum.Postgresql => 93,
                DatabaseTypeEnum.Mysql => 84,
                DatabaseTypeEnum.SqlServer => 87,
                _ => throw new NotSupportedException()
            };
            if (_Settings.Type == DatabaseTypeEnum.Mysql)
                return Armada.Core.Database.Mysql.Queries.TableQueries.MigrationV84Statements;

            List<SchemaMigration> migrations = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => Armada.Core.Database.Sqlite.Queries.TableQueries.GetMigrations(),
                DatabaseTypeEnum.Postgresql => Armada.Core.Database.Postgresql.Queries.TableQueries.GetMigrations(),
                _ => Armada.Core.Database.SqlServer.Queries.TableQueries.GetMigrations()
            };
            foreach (SchemaMigration migration in migrations)
            {
                if (migration.Version == version)
                    return System.Linq.Enumerable.ToArray(migration.Statements);
            }
            return Array.Empty<string>();
        }

        private async Task TestTenantCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                DateTime tiedCreation = DateTime.UtcNow.AddMinutes(1);
                TenantMetadata tenantA = await fixture.CreateTenantAsync("crud-tenant-a", token: token, createdUtc: tiedCreation).ConfigureAwait(false);
                TenantMetadata tenantB = await fixture.CreateTenantAsync("crud-tenant-b", token: token, createdUtc: tiedCreation).ConfigureAwait(false);

                TenantMetadata? read = await _Driver.Tenants.ReadAsync(tenantA.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Tenant read returned null");
                DatabaseAssert.HasPrefix(read.Id, "ten_", "Tenant.Id");
                DatabaseAssert.Equal(tenantA.Name, read.Name, "Tenant.Name");
                DatabaseAssert.Equal(tenantA.IsProtected, read.IsProtected, "Tenant.IsProtected");

                read.Name = tenantA.Name + " Updated";
                read.Active = false;
                TenantMetadata updated = await _Driver.Tenants.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(read.Name, updated.Name, "Updated Tenant.Name");
                DatabaseAssert.Equal(false, updated.Active, "Updated Tenant.Active");

                EnumerationResult<TenantMetadata> page1 = await _Driver.Tenants.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.True(page1.TotalRecords >= 2, "Tenant enumeration should include at least the two created tenants");
                DatabaseAssert.EnumerationPage(page1, 1, 1, page1.TotalRecords, (int)Math.Ceiling((double)page1.TotalRecords), 1);

                EnumerationResult<TenantMetadata> page2 = await _Driver.Tenants.EnumerateAsync(new EnumerationQuery { PageNumber = 2, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, page2.PageNumber, "Tenant page 2 number");
                DatabaseAssert.Equal(1, page2.Objects.Count, "Tenant page 2 object count");
                DatabaseAssert.ContainsIds(new[] { page1.Objects[0], page2.Objects[0] }, x => x.Id, tenantA.Id, tenantB.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestTenantLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("lookup-tenant", token: token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Tenants.ExistsAnyAsync(token).ConfigureAwait(false), "ExistsAnyAsync should return true after tenant creation");
                DatabaseAssert.True(await _Driver.Tenants.ExistsAsync(tenant.Id, token).ConfigureAwait(false), "Tenant ExistsAsync should return true");

                TenantMetadata? byName = await _Driver.Tenants.ReadByNameAsync(tenant.Name, token).ConfigureAwait(false);
                byName = DatabaseAssert.NotNull(byName, "Tenant ReadByNameAsync returned null");
                DatabaseAssert.Equal(tenant.Id, byName.Id, "Tenant.ReadByName.Id");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestUserCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("user-tenant", token: token).ConfigureAwait(false);
                UserMaster userA = await fixture.CreateUserAsync(tenant.Id, "usera", isTenantAdmin: true, token: token).ConfigureAwait(false);
                UserMaster userB = await fixture.CreateUserAsync(tenant.Id, "userb", token: token).ConfigureAwait(false);

                UserMaster? read = await _Driver.Users.ReadByIdAsync(userA.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "User read returned null");
                DatabaseAssert.HasPrefix(read.Id, "usr_", "User.Id");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "User.TenantId");
                DatabaseAssert.Equal(userA.Email, read.Email, "User.Email");
                DatabaseAssert.Equal(true, read.IsTenantAdmin, "User.IsTenantAdmin");

                read.FirstName = "Updated";
                read.LastName = "Person";
                read.Active = false;
                UserMaster updated = await _Driver.Users.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal("Updated", updated.FirstName, "Updated User.FirstName");
                DatabaseAssert.Equal("Person", updated.LastName, "Updated User.LastName");
                DatabaseAssert.Equal(false, updated.Active, "Updated User.Active");

                EnumerationResult<UserMaster> page1 = await _Driver.Users.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.EnumerationPage(page1, 1, 1, 2, 2, 1);
                EnumerationResult<UserMaster> page2 = await _Driver.Users.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 2, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.EnumerationPage(page2, 2, 1, 2, 2, 1);
                DatabaseAssert.ContainsIds(new[] { page1.Objects[0], page2.Objects[0] }, x => x.Id, userA.Id, userB.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestUserLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenantA = await fixture.CreateTenantAsync("user-lookup-a", token: token).ConfigureAwait(false);
                TenantMetadata tenantB = await fixture.CreateTenantAsync("user-lookup-b", token: token).ConfigureAwait(false);
                string sharedEmail = "shared-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.com";
                UserMaster userA = new UserMaster(tenantA.Id, sharedEmail, "password");
                userA.FirstName = "Shared";
                userA.LastName = "A";
                await _Driver.Users.CreateAsync(userA, token).ConfigureAwait(false);

                UserMaster userB = new UserMaster(tenantB.Id, sharedEmail, "password");
                userB.FirstName = "Shared";
                userB.LastName = "B";
                await _Driver.Users.CreateAsync(userB, token).ConfigureAwait(false);

                UserMaster? scoped = await _Driver.Users.ReadByEmailAsync(tenantA.Id, sharedEmail, token).ConfigureAwait(false);
                scoped = DatabaseAssert.NotNull(scoped, "User ReadByEmailAsync returned null");
                DatabaseAssert.Equal(userA.Id, scoped.Id, "User.ReadByEmail.Id");
                DatabaseAssert.True(await _Driver.Users.ExistsAsync(tenantA.Id, userA.Id, token).ConfigureAwait(false), "User ExistsAsync should return true");

                List<UserMaster> anyTenant = await _Driver.Users.ReadByEmailAnyTenantAsync(sharedEmail, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, anyTenant.Count, "ReadByEmailAnyTenant count");
                DatabaseAssert.ContainsIds(anyTenant, x => x.Id, userA.Id, userB.Id);

                if (!_NoCleanup)
                {
                    await _Driver.Users.DeleteAsync(tenantA.Id, userA.Id, token).ConfigureAwait(false);
                    await _Driver.Users.DeleteAsync(tenantB.Id, userB.Id, token).ConfigureAwait(false);
                }
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestCredentialCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("credential-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "credential-user", token: token).ConfigureAwait(false);
                Credential credentialA = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "credential-a", token: token).ConfigureAwait(false);
                Credential credentialB = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "credential-b", token: token).ConfigureAwait(false);

                Credential? read = await _Driver.Credentials.ReadByIdAsync(credentialA.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Credential read returned null");
                DatabaseAssert.HasPrefix(read.Id, "crd_", "Credential.Id");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "Credential.TenantId");
                DatabaseAssert.Equal(user.Id, read.UserId, "Credential.UserId");
                DatabaseAssert.Equal(credentialA.Name, read.Name, "Credential.Name");
                DatabaseAssert.True(!String.IsNullOrEmpty(read.BearerToken), "Credential.BearerToken missing");

                read.Name = "Renamed Credential";
                read.Active = false;
                Credential updated = await _Driver.Credentials.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal("Renamed Credential", updated.Name, "Updated Credential.Name");
                DatabaseAssert.Equal(false, updated.Active, "Updated Credential.Active");

                EnumerationResult<Credential> page1 = await _Driver.Credentials.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.EnumerationPage(page1, 1, 1, 2, 2, 1);
                EnumerationResult<Credential> page2 = await _Driver.Credentials.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 2, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.EnumerationPage(page2, 2, 1, 2, 2, 1);
                DatabaseAssert.ContainsIds(new[] { page1.Objects[0], page2.Objects[0] }, x => x.Id, credentialA.Id, credentialB.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestCredentialLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("credential-lookup", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "credential-lookup-user", token: token).ConfigureAwait(false);
                Credential credentialA = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "credential-lookup-a", token: token).ConfigureAwait(false);
                Credential credentialB = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "credential-lookup-b", token: token).ConfigureAwait(false);

                Credential? byToken = await _Driver.Credentials.ReadByBearerTokenAsync(credentialA.BearerToken, token).ConfigureAwait(false);
                byToken = DatabaseAssert.NotNull(byToken, "Credential ReadByBearerTokenAsync returned null");
                DatabaseAssert.Equal(credentialA.Id, byToken.Id, "Credential.ReadByBearerToken.Id");

                List<Credential> byUser = await _Driver.Credentials.EnumerateByUserAsync(tenant.Id, user.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, byUser.Count, "Credential EnumerateByUserAsync count");
                DatabaseAssert.ContainsIds(byUser, x => x.Id, credentialA.Id, credentialB.Id);

                EnumerationResult<Credential> paged = await _Driver.Credentials.EnumerateByUserAsync(tenant.Id, user.Id, new EnumerationQuery { PageNumber = 1, PageSize = 1 }, token).ConfigureAwait(false);
                DatabaseAssert.EnumerationPage(paged, 1, 1, 2, 2, 1);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestFleetCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                TenantMetadata tenant = graph.Tenant;
                UserMaster user = graph.User;
                Fleet fleet = graph.Fleet;
                Fleet? read = await _Driver.Fleets.ReadAsync(fleet.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Fleet read returned null");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "Fleet.TenantId");
                DatabaseAssert.Equal(user.Id, read.UserId, "Fleet.UserId");
                DatabaseAssert.Equal(fleet.Name, read.Name, "Fleet.Name");

                read.Description = "Updated fleet description";
                Fleet updated = await _Driver.Fleets.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal("Updated fleet description", updated.Description, "Fleet.Description");

                await AssertPagedTenantEnumerationAsync(
                    query => _Driver.Fleets.EnumerateAsync(tenant.Id, query, token),
                    fleet.Id,
                    async _ => await fixture.CreateFleetAsync(tenant.Id, user.Id, "page-two", token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestFleetLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Fleet fleet = graph.Fleet;
                Fleet? byName = await _Driver.Fleets.ReadByNameAsync(fleet.Name, token).ConfigureAwait(false);
                byName = DatabaseAssert.NotNull(byName, "Fleet ReadByNameAsync returned null");
                DatabaseAssert.Equal(fleet.Id, byName.Id, "Fleet.ReadByName.Id");
                DatabaseAssert.True(await _Driver.Fleets.ExistsAsync(fleet.Id, token).ConfigureAwait(false), "Fleet ExistsAsync should return true");

                List<Fleet> all = await _Driver.Fleets.EnumerateAsync(token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(all, x => x.Id, fleet.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestVesselCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                UserMaster user = graph.User;
                Fleet fleet = graph.Fleet;
                Vessel vessel = graph.Vessel;
                Vessel? read = await _Driver.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Vessel read returned null");
                DatabaseAssert.Equal(fleet.Id, read.FleetId, "Vessel.FleetId");
                DatabaseAssert.Equal(user.Id, read.UserId, "Vessel.UserId");
                DatabaseAssert.Equal(vessel.RepoUrl, read.RepoUrl, "Vessel.RepoUrl");

                read.DefaultBranch = "develop";
                read.AllowConcurrentMissions = true;
                read.ArchitectMaxMissionsPerVoyage = 7;
                read.ProtectedPaths = new List<string> { "src/guards/**", "config/release.json" };
                read.AutoLandPredicate = "fixture predicate";
                read.AutoLandCalibrationLandedCount = 13;
                read.SiblingRepos = "[\"sibling-example\"]";
                Vessel updated = await _Driver.Vessels.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal("develop", updated.DefaultBranch, "Vessel.DefaultBranch");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Vessel persisted = DatabaseAssert.NotNull(await reopened.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false), "Vessel reopened");
                    DatabaseAssert.True(persisted.AllowConcurrentMissions, "Vessel concurrency opt-in");
                    DatabaseAssert.Equal(read.ArchitectMaxMissionsPerVoyage, persisted.ArchitectMaxMissionsPerVoyage, "Vessel architecture bound");
                    DatabaseAssert.Equal(String.Join("|", read.ProtectedPaths), String.Join("|", persisted.ProtectedPaths), "Vessel protected paths");
                    DatabaseAssert.Equal(read.AutoLandPredicate, persisted.AutoLandPredicate, "Vessel landing predicate");
                    DatabaseAssert.Equal(read.AutoLandCalibrationLandedCount, persisted.AutoLandCalibrationLandedCount, "Vessel landing calibration");
                    DatabaseAssert.Equal(read.SiblingRepos, persisted.SiblingRepos, "Vessel sibling inputs");
                }

            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestVesselPreviewFieldAsync(string field, CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                System.Reflection.PropertyInfo property = typeof(Vessel).GetProperty(field)
                    ?? throw new InvalidOperationException("Missing fixture property " + field);
                object createdValue = property.PropertyType == typeof(bool) ? true
                    : property.PropertyType == typeof(List<string>) ? new List<string> { "protected/日本語/**", "stable" }
                    : "created/日本語/";
                object updatedValue = property.PropertyType == typeof(bool) ? false
                    : property.PropertyType == typeof(List<string>) ? new List<string> { "updated/**" }
                    : "updated/Unicode/";
                Vessel vessel = await fixture.CreateVesselAsync(graph.Tenant.Id, graph.User.Id, graph.Fleet.Id,
                    "preview", token, value => property.SetValue(value, createdValue)).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Vessel persisted = DatabaseAssert.NotNull(await reopened.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false), "Created preview vessel");
                    DatabaseAssert.Equal(System.Text.Json.JsonSerializer.Serialize(createdValue),
                        System.Text.Json.JsonSerializer.Serialize(property.GetValue(persisted)), field + " create/reopen");
                    property.SetValue(persisted, updatedValue);
                    await reopened.Vessels.UpdateAsync(persisted, token).ConfigureAwait(false);
                }
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Vessel persisted = DatabaseAssert.NotNull(await reopened.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false), "Updated preview vessel");
                    DatabaseAssert.Equal(System.Text.Json.JsonSerializer.Serialize(updatedValue),
                        System.Text.Json.JsonSerializer.Serialize(property.GetValue(persisted)), field + " update/reopen");
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }

        private async Task TestBackendFieldAsync(string entity, string field, CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Captain other = await fixture.CreateCaptainAsync(graph.Tenant.Id, graph.User.Id, "other-target", token).ConfigureAwait(false);
                object createdValue = field == "Tier" ? CaptainTierEnum.Premium
                    : field == "RequestedCaptainId" ? graph.Captain.Id : "created-日本語-" + Guid.NewGuid().ToString("N");
                object updatedValue = field == "Tier" ? CaptainTierEnum.Economy
                    : field == "RequestedCaptainId" ? other.Id : "updated-日本語-" + Guid.NewGuid().ToString("N");
                Type type = entity == "Captain" ? typeof(Captain) : entity == "Mission" ? typeof(Mission) : typeof(Voyage);
                System.Reflection.PropertyInfo property = type.GetProperty(field) ?? throw new InvalidOperationException(field);
                string id;
                if (entity == "Captain")
                {
                    Captain value = await fixture.CreateCaptainAsync(graph.Tenant.Id, graph.User.Id, "tier", token,
                        configure: item => property.SetValue(item, createdValue)).ConfigureAwait(false);
                    id = value.Id;
                }
                else if (entity == "Mission")
                {
                    Mission value = await fixture.CreateMissionAsync(graph.Tenant.Id, graph.User.Id, graph.Voyage.Id,
                        graph.Vessel.Id, graph.Captain.Id, "provenance", token,
                        configure: item => property.SetValue(item, createdValue)).ConfigureAwait(false);
                    id = value.Id;
                }
                else
                {
                    Voyage value = await fixture.CreateVoyageAsync(graph.Tenant.Id, graph.User.Id, "provenance", token,
                        configure: item => property.SetValue(item, createdValue)).ConfigureAwait(false);
                    id = value.Id;
                }
                object?[] expectedValues = new object?[] { createdValue, updatedValue, null };
                for (int step = 0; step < expectedValues.Length; step++)
                {
                    using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                    {
                        object persisted = entity switch
                        {
                            "Captain" => DatabaseAssert.NotNull(await reopened.Captains.ReadAsync(id, token).ConfigureAwait(false), "Captain missing"),
                            "Mission" => DatabaseAssert.NotNull(await reopened.Missions.ReadAsync(id, token).ConfigureAwait(false), "Mission missing"),
                            _ => DatabaseAssert.NotNull(await reopened.Voyages.ReadAsync(id, token).ConfigureAwait(false), "Voyage missing")
                        };
                        DatabaseAssert.Equal(expectedValues[step], property.GetValue(persisted), entity + "." + field + " reopen step " + step);
                        if (step + 1 == expectedValues.Length) continue;
                        property.SetValue(persisted, expectedValues[step + 1]);
                        if (persisted is Captain captain) await reopened.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
                        else if (persisted is Mission mission) await reopened.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                        else await reopened.Voyages.UpdateAsync((Voyage)persisted, token).ConfigureAwait(false);
                    }
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }

        private async Task TestCaptainQuarantineConditionalAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                string tenantId = graph.Tenant.Id;
                string userId = graph.User.Id;

                Captain idle = await CreateIdleCaptainAsync(fixture, tenantId, userId, "quarantine-idle", token).ConfigureAwait(false);
                DateTime until = new DateTime(2027, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                DatabaseAssert.True(await _Driver.Captains.TryQuarantineIdleAsync(idle.Id, "  operator hold  ", until, token).ConfigureAwait(false), "An idle captain can be quarantined");
                Captain held = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(idle.Id, token).ConfigureAwait(false), "Held captain");
                DatabaseAssert.Equal(CaptainStateEnum.Quarantined, held.State, "Held state");
                DatabaseAssert.Equal("operator hold", held.QuarantineReason, "Held reason is trimmed");
                DatabaseAssert.Equal(until, held.QuarantineUntilUtc, "Held expiry");

                DatabaseAssert.True(await _Driver.Captains.TryQuarantineIdleAsync(idle.Id, "indefinite hold", null, token).ConfigureAwait(false), "A repeated bench updates the hold");
                Captain indefinite = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(idle.Id, token).ConfigureAwait(false), "Indefinite captain");
                DatabaseAssert.Equal("indefinite hold", indefinite.QuarantineReason, "Updated reason");
                DatabaseAssert.True(!indefinite.QuarantineUntilUtc.HasValue, "A null expiry is an indefinite hold");

                DatabaseAssert.True(!await _Driver.Captains.TryQuarantineIdleAsync(
                    idle.Id, "shorter crash hold", DateTime.UtcNow.AddMinutes(5), token, true).ConfigureAwait(false),
                    "A conditional crash hold cannot replace an indefinite hold");
                DatabaseAssert.Equal("indefinite hold", (await _Driver.Captains.ReadAsync(idle.Id, token).ConfigureAwait(false))!.QuarantineReason,
                    "The indefinite hold remains after a refused crash hold");

                Captain extendable = await CreateIdleCaptainAsync(fixture, tenantId, userId, "quarantine-extendable", token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Captains.TryQuarantineIdleAsync(extendable.Id, "short hold", DateTime.UtcNow.AddMinutes(5), token).ConfigureAwait(false), "Short hold");
                DatabaseAssert.True(await _Driver.Captains.TryQuarantineIdleAsync(extendable.Id, "long crash hold", DateTime.UtcNow.AddMinutes(30), token, true).ConfigureAwait(false), "A conditional crash hold extends a shorter hold");
                Captain extended = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(extendable.Id, token).ConfigureAwait(false), "Extended captain");
                DatabaseAssert.Equal("long crash hold", extended.QuarantineReason, "The longer crash hold is stored");

                Captain claimed = await CreateIdleCaptainAsync(fixture, tenantId, userId, "quarantine-claimed", token).ConfigureAwait(false);
                Dock claimedDock = await fixture.CreateDockAsync(tenantId, userId, graph.Vessel.Id, claimed.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Captains.TryClaimAsync(tenantId, claimed.Id, graph.Mission.Id, claimedDock.Id, token).ConfigureAwait(false), "Claim the captain");
                DatabaseAssert.True(!await _Driver.Captains.TryQuarantineIdleAsync(claimed.Id, "must refuse", null, token).ConfigureAwait(false), "A claimed captain is not quarantined");
                Captain stillWorking = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(claimed.Id, token).ConfigureAwait(false), "Claimed captain");
                DatabaseAssert.Equal(CaptainStateEnum.Working, stillWorking.State, "Claimed state is unchanged");
                DatabaseAssert.Equal(graph.Mission.Id, stillWorking.CurrentMissionId, "Claimed mission is kept");
                DatabaseAssert.Equal(claimedDock.Id, stillWorking.CurrentDockId, "Claimed dock is kept");
                DatabaseAssert.True(stillWorking.QuarantineReason == null, "No reason is written on refusal");

                Captain processOwner = await CreateIdleCaptainAsync(fixture, tenantId, userId, "quarantine-process", token).ConfigureAwait(false);
                processOwner.ProcessId = 4242;
                await _Driver.Captains.UpdateAsync(processOwner, token).ConfigureAwait(false);
                DatabaseAssert.True(!await _Driver.Captains.TryQuarantineIdleAsync(processOwner.Id, "must refuse", null, token).ConfigureAwait(false), "A captain with a process is not quarantined");
                Captain stillOwning = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(processOwner.Id, token).ConfigureAwait(false), "Process owner");
                DatabaseAssert.Equal(4242, stillOwning.ProcessId, "Process is kept");

                DatabaseAssert.True(!await _Driver.Captains.TryReleaseTimedQuarantineAsync(idle.Id, null, token).ConfigureAwait(false), "An indefinite hold is not released by a timed release");
                Captain timed = await CreateIdleCaptainAsync(fixture, tenantId, userId, "quarantine-timed", token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Captains.TryQuarantineIdleAsync(timed.Id, "timed hold", DateTime.UtcNow.AddHours(1), token).ConfigureAwait(false), "Timed hold");
                DatabaseAssert.True(!await _Driver.Captains.TryReleaseTimedQuarantineAsync(timed.Id, DateTime.UtcNow, token).ConfigureAwait(false), "An unexpired hold is not released by the expiry sweep");
                DatabaseAssert.True(await _Driver.Captains.TryReleaseTimedQuarantineAsync(timed.Id, DateTime.UtcNow.AddHours(2), token).ConfigureAwait(false), "An expired hold is released by the expiry sweep");
                Captain probed = await CreateIdleCaptainAsync(fixture, tenantId, userId, "quarantine-probed", token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Captains.TryQuarantineIdleAsync(probed.Id, "probe hold", DateTime.UtcNow.AddHours(1), token).ConfigureAwait(false), "Probe hold");
                DatabaseAssert.True(await _Driver.Captains.TryReleaseTimedQuarantineAsync(probed.Id, null, token).ConfigureAwait(false), "A probe releases a timed hold early");

                DatabaseAssert.True(await _Driver.Captains.TryReleaseQuarantineAsync(idle.Id, token).ConfigureAwait(false), "A quarantined captain is released");
                DatabaseAssert.True(!await _Driver.Captains.TryReleaseQuarantineAsync(idle.Id, token).ConfigureAwait(false), "A repeated release changes nothing");
                DatabaseAssert.True(!await _Driver.Captains.TryReleaseQuarantineAsync(claimed.Id, token).ConfigureAwait(false), "A working captain is not released to Idle");

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Captain released = DatabaseAssert.NotNull(await reopened.Captains.ReadAsync(idle.Id, token).ConfigureAwait(false), "Released captain reopened");
                    DatabaseAssert.Equal(CaptainStateEnum.Idle, released.State, "Released state persists");
                    DatabaseAssert.True(released.QuarantineReason == null && !released.QuarantineUntilUtc.HasValue, "Release clears reason and expiry");
                    Captain working = DatabaseAssert.NotNull(await reopened.Captains.ReadAsync(claimed.Id, token).ConfigureAwait(false), "Working captain reopened");
                    DatabaseAssert.Equal(CaptainStateEnum.Working, working.State, "Working state persists");
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }

        private async Task TestMergeEntryScopedMissionFilterAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                string tenantId = graph.Tenant.Id;
                string userId = graph.User.Id;
                Mission otherMission = await fixture.CreateMissionAsync(tenantId, userId, graph.Voyage.Id, graph.Vessel.Id, graph.Captain.Id, "scoped-filter-mission", token).ConfigureAwait(false);
                MergeEntry otherEntry = await fixture.CreateMergeEntryAsync(tenantId, userId, otherMission.Id, graph.Vessel.Id, token).ConfigureAwait(false);

                EnumerationQuery byMission = new EnumerationQuery { MissionId = otherMission.Id, PageNumber = 1, PageSize = 50 };
                EnumerationResult<MergeEntry> tenantScoped = await _Driver.MergeEntries.EnumerateAsync(tenantId, byMission, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, tenantScoped.Objects.Count, "Tenant-scoped enumerate returns only the requested mission's entries");
                DatabaseAssert.Equal(otherEntry.Id, tenantScoped.Objects[0].Id, "Tenant-scoped enumerate returns the requested mission's entry");

                EnumerationResult<MergeEntry> userScoped = await _Driver.MergeEntries.EnumerateAsync(tenantId, userId, byMission, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, userScoped.Objects.Count, "Tenant and user scoped enumerate returns only the requested mission's entries");
                DatabaseAssert.Equal(otherEntry.Id, userScoped.Objects[0].Id, "Tenant and user scoped enumerate returns the requested mission's entry");

                EnumerationResult<MergeEntry> byVessel = await _Driver.MergeEntries.EnumerateAsync(tenantId, new EnumerationQuery { VesselId = "vsl_not_this_vessel", PageNumber = 1, PageSize = 50 }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0, byVessel.Objects.Count, "Tenant-scoped enumerate honors the vessel filter");
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }

        private async Task<Captain> CreateIdleCaptainAsync(DatabaseFixture fixture, string tenantId, string userId, string name, CancellationToken token)
        {
            Captain created = await fixture.CreateCaptainAsync(tenantId, userId, name, token).ConfigureAwait(false);
            Captain idle = DatabaseAssert.NotNull(await _Driver.Captains.ReadAsync(created.Id, token).ConfigureAwait(false), "Captain " + name);
            idle.State = CaptainStateEnum.Idle;
            idle.CurrentMissionId = null;
            idle.CurrentDockId = null;
            idle.ProcessId = null;
            idle.QuarantineReason = null;
            idle.QuarantineUntilUtc = null;
            return await _Driver.Captains.UpdateAsync(idle, token).ConfigureAwait(false);
        }

        private async Task TestCaptainCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                TenantMetadata tenant = graph.Tenant;
                UserMaster user = graph.User;
                Captain captain = graph.Captain;
                Captain? read = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Captain read returned null");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "Captain.TenantId");
                DatabaseAssert.Equal(user.Id, read.UserId, "Captain.UserId");
                DatabaseAssert.Equal(captain.Name, read.Name, "Captain.Name");

                Captain modeledCaptain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "modeled-captain", token: token, model: "gpt-5.4").ConfigureAwait(false);
                Captain? modeledRead = await _Driver.Captains.ReadAsync(modeledCaptain.Id, token).ConfigureAwait(false);
                modeledRead = DatabaseAssert.NotNull(modeledRead, "Modeled captain read returned null");
                DatabaseAssert.Equal("gpt-5.4", modeledRead.Model, "Captain.Model");

                read.RecoveryAttempts = 2;
                read.Model = "gpt-5.4-mini";
                read.AllowedPersonas = "Worker,Reviewer";
                read.PreferredPersona = "Reviewer";
                read.RuntimeOptionsJson = "{\"fixture\":true}";
                read.QuarantineUntilUtc = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
                read.QuarantineReason = "Fixture quarantine ownership";
                read.ApiKey = "fixture-key";
                read.ApiBaseUrl = "https://provider.example.test/api";

                Captain updated = await _Driver.Captains.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2, updated.RecoveryAttempts, "Captain.RecoveryAttempts");
                DatabaseAssert.Equal("gpt-5.4-mini", updated.Model, "Updated Captain.Model");
                Captain? updatedRead = await _Driver.Captains.ReadAsync(read.Id, token).ConfigureAwait(false);
                updatedRead = DatabaseAssert.NotNull(updatedRead, "Updated captain read returned null");
                DatabaseAssert.Equal("gpt-5.4-mini", updatedRead.Model, "Persisted Captain.Model");
                await _Driver.Captains.UpdateProcessAliveAsync(read.Id, token).ConfigureAwait(false);
                // A stale ordinary update must not erase the independently refreshed liveness.
                await _Driver.Captains.UpdateAsync(read, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Captain persisted = DatabaseAssert.NotNull(await reopened.Captains.ReadAsync(read.Id, token).ConfigureAwait(false), "Captain reopened");
                    DatabaseAssert.Equal(read.AllowedPersonas, persisted.AllowedPersonas, "Captain persona constraints");
                    DatabaseAssert.Equal(read.PreferredPersona, persisted.PreferredPersona, "Captain preferred persona");
                    DatabaseAssert.Equal(read.RuntimeOptionsJson, persisted.RuntimeOptionsJson, "Captain runtime options");
                    DatabaseAssert.Equal(read.QuarantineUntilUtc, persisted.QuarantineUntilUtc, "Captain quarantine UTC microseconds");
                    DatabaseAssert.Equal(read.QuarantineReason, persisted.QuarantineReason, "Captain quarantine reason");
                    DatabaseAssert.Equal(read.ApiKey, persisted.ApiKey, "Captain provider key");
                    DatabaseAssert.Equal(read.ApiBaseUrl, persisted.ApiBaseUrl, "Captain provider URL");
                    DatabaseAssert.True(persisted.LastProcessAliveUtc.HasValue, "Captain independent liveness persists");
                    DatabaseAssert.Equal(read.LastHeartbeatUtc, persisted.LastHeartbeatUtc, "Liveness does not advance output heartbeat");
                }

            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestMemoryCrudAsync(CancellationToken token)
        {
            string tenantId = "ten_memory_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Memory memory = new Memory();
            memory.TenantId = tenantId;
            memory.UserId = "usr_memory";
            memory.Type = MemoryTypeEnum.Procedural;
            memory.Scope = MemoryScopeEnum.UserSpecific;
            memory.Topic = "build";
            memory.Key = "build/quiet-window";
            memory.Summary = "Quiet window 日本語";
            memory.Content = "Re-run a single failure alone before triage. 日本語";
            memory.Salience = 0.9;
            memory.SourceKind = MemorySourceKindEnum.Voyage;
            memory.SourceVoyageId = "vyg_example";
            memory.VesselId = "vsl_example";
            memory.Tags = new List<string> { "tests", "日本語" };

            Memory created = await _Driver.Memories.CreateAsync(memory, token).ConfigureAwait(false);
            try
            {
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Memory stored = DatabaseAssert.NotNull(await reopened.Memories.ReadAsync(created.Id, token).ConfigureAwait(false), "Memory retained");
                    DatabaseAssert.Equal(memory.Content, stored.Content, "Unicode content");
                    DatabaseAssert.Equal("build/quiet-window", stored.Key, "Key");
                    DatabaseAssert.Equal(MemoryTypeEnum.Procedural, stored.Type, "Type");
                    DatabaseAssert.Equal(MemoryScopeEnum.UserSpecific, stored.Scope, "Scope");
                    DatabaseAssert.Equal(MemorySourceKindEnum.Voyage, stored.SourceKind, "Source kind");
                    DatabaseAssert.Equal(0.9, stored.Salience, "Salience");
                    DatabaseAssert.Equal(1, stored.Version, "Initial version");
                    DatabaseAssert.Equal(2, stored.Tags.Count, "Tag count");

                    stored.Content = "corrected content";
                    stored.Tags = new List<string> { "single" };
                    stored.Version = stored.Version + 1;
                    stored.LastUpdateUtc = DateTime.UtcNow;
                    DatabaseAssert.True(await reopened.Memories.UpdateAsync(stored, 1, token).ConfigureAwait(false), "Guarded update applies");

                    Memory updated = DatabaseAssert.NotNull(await reopened.Memories.ReadAsync(tenantId, created.Id, token).ConfigureAwait(false), "Updated memory retained");
                    DatabaseAssert.Equal("corrected content", updated.Content, "Updated content");
                    DatabaseAssert.Equal(1, updated.Tags.Count, "Tags replaced");
                    DatabaseAssert.Equal(2, updated.Version, "Version advanced once");
                }
            }
            finally
            {
                if (!_NoCleanup) await _Driver.Memories.DeleteAsync(tenantId, created.Id, token).ConfigureAwait(false);
            }
        }

        private async Task TestMemoryScopingAsync(CancellationToken token)
        {
            string tenantA = "ten_memory_a_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tenantB = "ten_memory_b_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Memory first = new Memory { TenantId = tenantA, UserId = "usr_a", Content = "tenant A finding", Key = "shared/key" };
            Memory second = new Memory { TenantId = tenantB, UserId = "usr_b", Content = "tenant B finding", Key = "shared/key" };
            Memory createdA = await _Driver.Memories.CreateAsync(first, token).ConfigureAwait(false);
            Memory createdB = await _Driver.Memories.CreateAsync(second, token).ConfigureAwait(false);

            try
            {
                DatabaseAssert.Equal(createdA.Id,
                    DatabaseAssert.NotNull(await _Driver.Memories.ReadByKeyAsync(tenantA, "shared/key", token).ConfigureAwait(false), "Key resolves in its tenant").Id,
                    "One key per tenant resolves to that tenant's record");
                DatabaseAssert.True(await _Driver.Memories.ReadAsync(tenantB, createdA.Id, token).ConfigureAwait(false) == null, "No cross-tenant read");
                DatabaseAssert.True(!await _Driver.Memories.DeleteAsync(tenantB, createdA.Id, token).ConfigureAwait(false), "No cross-tenant delete");

                Memory duplicate = new Memory { TenantId = tenantA, UserId = "usr_a", Content = "duplicate", Key = "shared/key" };
                bool rejected = false;
                try { await _Driver.Memories.CreateAsync(duplicate, token).ConfigureAwait(false); }
                catch (DbException) { rejected = true; }
                DatabaseAssert.True(rejected, "A duplicate key inside one tenant is rejected");

                Memory stored = DatabaseAssert.NotNull(await _Driver.Memories.ReadAsync(tenantA, createdA.Id, token).ConfigureAwait(false), "Record retained");
                stored.Content = "first writer";
                stored.Version = 2;
                DatabaseAssert.True(await _Driver.Memories.UpdateAsync(stored, 1, token).ConfigureAwait(false), "First guarded update applies");
                stored.Content = "second writer";
                stored.Version = 2;
                DatabaseAssert.True(!await _Driver.Memories.UpdateAsync(stored, 1, token).ConfigureAwait(false), "Stale guarded update is refused");
                DatabaseAssert.Equal("first writer",
                    DatabaseAssert.NotNull(await _Driver.Memories.ReadAsync(tenantA, createdA.Id, token).ConfigureAwait(false), "Record retained").Content,
                    "The refused update changed nothing");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.Memories.DeleteAsync(tenantA, createdA.Id, token).ConfigureAwait(false);
                    await _Driver.Memories.DeleteAsync(tenantB, createdB.Id, token).ConfigureAwait(false);
                }
            }
        }

        private async Task TestConfigurationOwnershipAsync(CancellationToken token)
        {
            // Personas, pipelines and prompt templates store an owning user and an ownership scope.
            // Both must survive create, update and a reopen, with full Unicode identifiers.
            TenantMetadata tenant = new TenantMetadata("ownership 日本語")
            {
                Id = "ten_ownership_" + Guid.NewGuid().ToString("N").Substring(0, 8)
            };
            await _Driver.Tenants.CreateAsync(tenant, token).ConfigureAwait(false);
            string userId = "usr_ownership_日本語_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

            Persona persona = new Persona("ownership-persona-" + suffix, "persona.worker")
            {
                TenantId = tenant.Id, UserId = userId, OwnershipScope = OwnershipScopeEnum.UserSpecific
            };
            Pipeline pipeline = new Pipeline("ownership-pipeline-" + suffix)
            {
                TenantId = tenant.Id, UserId = userId, OwnershipScope = OwnershipScopeEnum.UserSpecific
            };
            pipeline.Stages.Add(new PipelineStage(1, "Worker"));
            PromptTemplate template = new PromptTemplate("ownership.template." + suffix, "content")
            {
                TenantId = tenant.Id, UserId = userId, OwnershipScope = OwnershipScopeEnum.UserSpecific
            };
            Persona legacyDefault = new Persona("ownership-default-" + suffix, "persona.worker") { TenantId = tenant.Id };

            await _Driver.Personas.CreateAsync(persona, token).ConfigureAwait(false);
            await _Driver.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);
            await _Driver.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
            await _Driver.Personas.CreateAsync(legacyDefault, token).ConfigureAwait(false);
            try
            {
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Persona storedPersona = DatabaseAssert.NotNull(await reopened.Personas.ReadAsync(persona.Id, token).ConfigureAwait(false), "Persona retained");
                    DatabaseAssert.Equal(userId, storedPersona.UserId, "Persona Unicode owner");
                    DatabaseAssert.Equal(OwnershipScopeEnum.UserSpecific, storedPersona.OwnershipScope, "Persona scope");

                    Pipeline storedPipeline = DatabaseAssert.NotNull(await reopened.Pipelines.ReadByNameAsync(tenant.Id, pipeline.Name, token).ConfigureAwait(false), "Pipeline retained");
                    DatabaseAssert.Equal(userId, storedPipeline.UserId, "Pipeline owner");
                    DatabaseAssert.Equal(OwnershipScopeEnum.UserSpecific, storedPipeline.OwnershipScope, "Pipeline scope");

                    PromptTemplate storedTemplate = DatabaseAssert.NotNull(await reopened.PromptTemplates.ReadAsync(template.Id, token).ConfigureAwait(false), "Template retained");
                    DatabaseAssert.Equal(userId, storedTemplate.UserId, "Template owner");
                    DatabaseAssert.Equal(OwnershipScopeEnum.UserSpecific, storedTemplate.OwnershipScope, "Template scope");

                    Persona storedDefault = DatabaseAssert.NotNull(await reopened.Personas.ReadAsync(legacyDefault.Id, token).ConfigureAwait(false), "Default persona retained");
                    DatabaseAssert.True(storedDefault.UserId == null, "A record created without an owner keeps a null user");
                    DatabaseAssert.Equal(OwnershipScopeEnum.TenantWide, storedDefault.OwnershipScope, "A record created without a scope is tenant-wide");

                    storedPersona.OwnershipScope = OwnershipScopeEnum.TenantWide;
                    storedPersona.UserId = null;
                    await reopened.Personas.UpdateAsync(storedPersona, token).ConfigureAwait(false);
                    Persona updated = DatabaseAssert.NotNull(await reopened.Personas.ReadAsync(persona.Id, token).ConfigureAwait(false), "Updated persona retained");
                    DatabaseAssert.Equal(OwnershipScopeEnum.TenantWide, updated.OwnershipScope, "Scope update applies");
                    DatabaseAssert.True(updated.UserId == null, "Owner update applies");
                }
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.Personas.DeleteAsync(persona.Id, token).ConfigureAwait(false);
                    await _Driver.Personas.DeleteAsync(legacyDefault.Id, token).ConfigureAwait(false);
                    await _Driver.Pipelines.DeleteAsync(pipeline.Id, token).ConfigureAwait(false);
                    await _Driver.PromptTemplates.DeleteAsync(template.Id, token).ConfigureAwait(false);
                    await _Driver.Tenants.DeleteAsync(tenant.Id, token).ConfigureAwait(false);
                }
            }
        }

        private async Task TestMissionAdmissionUnicodeAsync(CancellationToken token)
        {
            TenantMetadata tenant = new TenantMetadata("admission Unicode")
            {
                Id = Guid.NewGuid().ToString("N") + new string('界', 418)
            };
            await _Driver.Tenants.CreateAsync(tenant, token);
            Mission mission = new Mission("Unicode admission", "Full identifiers")
            {
                Id = Guid.NewGuid().ToString("N") + new string('雪', 418),
                TenantId = tenant.Id, UserId = null
            };
            try
            {
                await _Driver.Missions.CreateAsync(mission, token);
                Mission loaded = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
                MissionAdmissionObservation observation = new MissionAdmissionObservation
                {
                    MissionId = loaded.Id, TenantId = loaded.TenantId, UserId = loaded.UserId,
                    VesselId = loaded.VesselId, Admit = false, Reason = new string('語', 1000),
                    PressureDecision = new ResourcePressureDecision { Admit = false, Reason = new string('界', 1000) }
                };
                DatabaseAssert.True(await _Driver.Missions.TryRecordAdmissionAsync(loaded, observation, token), "Valid long Unicode identifiers and reasons fit the evidence bound");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token))
                {
                    Mission stored = (await reopened.Missions.ReadAsync(mission.Id, token))!;
                    DatabaseAssert.Equal(mission.Id, stored.LastAdmissionObservation?.MissionId, "Full mission identifier survives");
                    DatabaseAssert.Equal(tenant.Id, stored.LastAdmissionObservation?.TenantId, "Full tenant identifier survives");
                    DatabaseAssert.Equal(observation.Reason, stored.LastAdmissionObservation?.Reason, "Full bounded Unicode reason survives");
                }
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.Missions.DeleteAsync(mission.Id, token);
                    await _Driver.Tenants.DeleteAsync(tenant.Id, token);
                }
            }
        }

        private async Task TestMissionAdmissionAsync(CancellationToken token)
        {
            Mission mission = await _Driver.Missions.CreateAsync(new Mission("admission 日本語", "Historical evidence"), token);
            Mission loaded = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            DatabaseAssert.True(loaded.LastAdmissionObservation == null, "New missions have no invented observation");
            MissionAdmissionObservation observation = new MissionAdmissionObservation
            {
                MissionId = loaded.Id, TenantId = loaded.TenantId, UserId = loaded.UserId, VesselId = loaded.VesselId,
                Admit = false, GlobalActiveWorkloads = 3, GlobalWorkloadLimit = 3, GlobalLimitReached = true,
                Reason = "Global limit 日本語"
            };
            DatabaseAssert.True(await _Driver.Missions.TryRecordAdmissionAsync(loaded, observation, token), "First observation recorded");
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token))
            {
                Mission stored = (await reopened.Missions.ReadAsync(mission.Id, token))!;
                DatabaseAssert.Equal(observation.ObservationId, stored.LastAdmissionObservation?.ObservationId, "Observation survives reopen");
                DatabaseAssert.Equal(observation.Reason, stored.LastAdmissionObservation?.Reason, "Unicode explanation survives reopen");
                MissionSummary summary = (await reopened.Missions.EnumerateMissionSummariesAsync(new EnumerationQuery { CreatedAfter = mission.CreatedUtc.AddSeconds(-1), PageSize = 100 }, token)).Objects.Find(value => value.Id == mission.Id)!;
                DatabaseAssert.Equal(observation.ObservationId, summary.LastAdmissionObservation?.ObservationId, "Lightweight summary exposes the recorded decision");
                DatabaseAssert.Equal(MissionAssignmentStateEnum.WaitingForResourcePressure, stored.AssignmentState, "Refusal and waiting state persist together");
                DatabaseAssert.True(stored.CaptainId == null && stored.ProcessId == null, "Evidence write cannot acquire process ownership");
            }
            DatabaseAssert.True(!await _Driver.Missions.TryRecordAdmissionAsync(loaded, observation, token), "Stale observation cannot overwrite a newer write");
            Mission beforeCycle = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            Mission changed = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            changed.Status = MissionStatusEnum.Assigned;
            await _Driver.Missions.UpdateAsync(changed, token);
            changed.Status = MissionStatusEnum.Pending;
            await _Driver.Missions.UpdateAsync(changed, token);
            using (System.Data.Common.DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE missions SET last_update_utc=@stamp WHERE id=@id;";
                    System.Data.Common.DbParameter stamp = command.CreateParameter(); stamp.ParameterName = "@stamp";
                    stamp.Value = _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                        ? beforeCycle.LastUpdateUtc.ToString("o") : beforeCycle.LastUpdateUtc;
                    command.Parameters.Add(stamp);
                    System.Data.Common.DbParameter identifier = command.CreateParameter(); identifier.ParameterName = "@id"; identifier.Value = mission.Id;
                    command.Parameters.Add(identifier);
                    await command.ExecuteNonQueryAsync(token);
                }
            }
            DatabaseAssert.True(!await _Driver.Missions.TryRecordAdmissionAsync(beforeCycle, observation, token), "An old snapshot cannot survive a state cycle even with its timestamp restored");
            Mission contenderA = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            Mission contenderB = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            bool[] results = await Task.WhenAll(_Driver.Missions.TryRecordAdmissionAsync(contenderA, observation, token),
                _Driver.Missions.TryRecordAdmissionAsync(contenderB, observation, token));
            DatabaseAssert.Equal(1, Array.FindAll(results, value => value).Length, "Only one concurrent observation wins");
            Mission beforeHeartbeat = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            await _Driver.Missions.UpdateHeartbeatAsync(mission.Id, token);
            DatabaseAssert.True(!await _Driver.Missions.TryRecordAdmissionAsync(beforeHeartbeat, observation, token), "Heartbeat invalidates the observation snapshot");
            Mission current = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            observation.GlobalWorkloadLimit = 0;
            bool rejected = false;
            try { await _Driver.Missions.TryRecordAdmissionAsync(current, observation, token); }
            catch (InvalidOperationException) { rejected = true; }
            DatabaseAssert.True(rejected, "An unlimited policy cannot report a reached global limit");
            observation.GlobalWorkloadLimit = 3;
            observation.Reason = "Bearer " + new string('a', 24);
            using (System.Data.Common.DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token);
                using (System.Data.Common.DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE missions SET last_admission_json=@json WHERE id=@id;";
                    System.Data.Common.DbParameter payload = command.CreateParameter(); payload.ParameterName = "@json";
                    payload.Value = System.Text.Json.JsonSerializer.Serialize(observation); command.Parameters.Add(payload);
                    System.Data.Common.DbParameter identifier = command.CreateParameter(); identifier.ParameterName = "@id"; identifier.Value = mission.Id;
                    command.Parameters.Add(identifier);
                    await command.ExecuteNonQueryAsync(token);
                    Mission redacted = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
                    DatabaseAssert.True(!redacted.LastAdmissionObservation!.Reason.Contains(new string('a', 24)), "Read redacts stored secrets");
                    observation.GlobalWorkloadLimit = 0;
                    payload.Value = System.Text.Json.JsonSerializer.Serialize(observation);
                    await command.ExecuteNonQueryAsync(token);
                    Mission invalid = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
                    DatabaseAssert.True(invalid.LastAdmissionObservation == null, "Contradictory stored evidence is unavailable");
                    observation.Version = 99;
                    payload.Value = System.Text.Json.JsonSerializer.Serialize(observation);
                    await command.ExecuteNonQueryAsync(token);
                    DatabaseAssert.True((await _Driver.Missions.ReadAsync(mission.Id, token))!.LastAdmissionObservation == null, "Unknown evidence version is unavailable");
                    payload.Value = "invalid JSON";
                    await command.ExecuteNonQueryAsync(token);
                    DatabaseAssert.True((await _Driver.Missions.ReadAsync(mission.Id, token))!.LastAdmissionObservation == null, "Malformed evidence is unavailable");
                }
            }
            observation.Version = 1;
            observation.GlobalWorkloadLimit = 3;
            Mission restored = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            DatabaseAssert.True(await _Driver.Missions.TryRecordAdmissionAsync(restored, observation, token), "Fresh observation replaces unavailable evidence");
            Mission staleOwner = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            Mission processOwner = (await _Driver.Missions.ReadAsync(mission.Id, token))!;
            processOwner.ProcessId = 4242;
            await _Driver.Missions.UpdateAsync(processOwner, token);
            DatabaseAssert.True(!await _Driver.Missions.TryRecordAdmissionAsync(staleOwner, observation, token), "Ownership change rejects the stale evaluation");
            DatabaseAssert.Equal(4242, (await _Driver.Missions.ReadAsync(mission.Id, token))!.ProcessId, "Rejected evidence cannot overwrite process ownership");
            Vessel movedVessel = await _Driver.Vessels.CreateAsync(new Vessel("admission moved", "https://example.invalid/moved"), token);
            processOwner.VesselId = movedVessel.Id;
            await _Driver.Missions.UpdateAsync(processOwner, token);
            DatabaseAssert.True((await _Driver.Missions.ReadAsync(mission.Id, token))!.LastAdmissionObservation == null, "Vessel ownership change clears evidence");
            await _Driver.Missions.UpdateAsync(staleOwner, token);
            DatabaseAssert.True((await _Driver.Missions.ReadAsync(mission.Id, token))!.LastAdmissionObservation == null, "An old DTO cannot restore stored evidence");
            await _Driver.Missions.DeleteAsync(mission.Id, token);
            await _Driver.Vessels.DeleteAsync(movedVessel.Id, token);
        }

        private async Task TestDockAnchorSnapshotAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Dock dock = await fixture.CreateDockAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, graph.Captain.Id,
                    token, configure: value => value.GitAnchorsSnapshot = new DockGitAnchorSnapshot
                    {
                        DockId = value.Id, MissionId = graph.Mission.Id, VesselId = graph.Vessel.Id,
                        ProvisionedCommit = new string('a', 40), ProvisionedUtc = DateTime.UtcNow,
                        Anchors = new GitAnchors { BaseCommit = new string('a', 40), TargetBranch = "feature/日本語" }
                    }).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Dock stored = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Dock retained");
                    DockGitAnchorSnapshot snapshot = DatabaseAssert.NotNull(stored.GitAnchorsSnapshot, "Provisioning snapshot survives reopen");
                    DatabaseAssert.Equal(dock.GitAnchorsSnapshot!.ProvisionedCommit, snapshot.ProvisionedCommit, "Full provisioning commit");
                    DatabaseAssert.Equal(dock.Id, snapshot.DockId, "Snapshot dock association");
                    DatabaseAssert.Equal(graph.Mission.Id, snapshot.MissionId, "Snapshot mission association");
                    DatabaseAssert.Equal("feature/日本語", snapshot.Anchors.TargetBranch, "Unicode snapshot content");
                    DatabaseAssert.Equal(DockGitAnchorStateEnum.Seeded, snapshot.State, "Unresolved seed remains explicit");
                    Dock legacy = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(graph.Dock.Id, token).ConfigureAwait(false), "Legacy dock retained");
                    DatabaseAssert.True(legacy.GitAnchorsSnapshot == null, "Absent legacy evidence remains unavailable");

                    DockGitAnchorSnapshot first = System.Text.Json.JsonSerializer.Deserialize<DockGitAnchorSnapshot>(System.Text.Json.JsonSerializer.Serialize(snapshot))!;
                    first.State = DockGitAnchorStateEnum.Complete;
                    first.ResolvedUtc = DateTime.UtcNow;
                    first.Anchors.TargetTip = new string('b', 40);
                    DockGitAnchorSnapshot second = System.Text.Json.JsonSerializer.Deserialize<DockGitAnchorSnapshot>(System.Text.Json.JsonSerializer.Serialize(first))!;
                    second.Anchors.TargetTip = new string('c', 40);
                    snapshot.ProvisionedCommit = new string('d', 40);
                    snapshot.Anchors.BaseCommit = snapshot.ProvisionedCommit;
                    first.ProvisionedCommit = snapshot.ProvisionedCommit;
                    first.Anchors.BaseCommit = snapshot.ProvisionedCommit;
                    bool changedSeedRejected = false;
                    try { await reopened.Docks.TryCompleteGitAnchorsAsync(dock.Id, graph.Captain.Id, snapshot, first, token).ConfigureAwait(false); }
                    catch (InvalidOperationException) { changedSeedRejected = true; }
                    DatabaseAssert.True(changedSeedRejected, "Modified loaded seed cannot rewrite provisioning identity");
                    snapshot.ProvisionedCommit = new string('a', 40);
                    snapshot.Anchors.BaseCommit = snapshot.ProvisionedCommit;
                    first.ProvisionedCommit = snapshot.ProvisionedCommit;
                    first.Anchors.BaseCommit = snapshot.ProvisionedCommit;
                    DatabaseAssert.True(!await reopened.Docks.TryCompleteGitAnchorsAsync(dock.Id, "wrong-owner", snapshot, first, token).ConfigureAwait(false), "Wrong captain cannot enrich");
                    bool[] results = await Task.WhenAll(
                        reopened.Docks.TryCompleteGitAnchorsAsync(dock.Id, graph.Captain.Id, snapshot, first, token),
                        _Driver.Docks.TryCompleteGitAnchorsAsync(dock.Id, graph.Captain.Id, snapshot, second, token)).ConfigureAwait(false);
                    DatabaseAssert.Equal(1, System.Linq.Enumerable.Count(results, value => value), "Only one concurrent completion wins");
                    stored = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Completed dock retained");
                    DatabaseAssert.Equal(results[0] ? first.Anchors.TargetTip : second.Anchors.TargetTip,
                        stored.GitAnchorsSnapshot!.Anchors.TargetTip, "Winning snapshot is retained");
                    DatabaseAssert.Equal(graph.Captain.Id, stored.CaptainId, "Enrichment preserves captain ownership");
                    DatabaseAssert.Equal(dock.WorktreePath, stored.WorktreePath, "Enrichment preserves worktree");
                    DatabaseAssert.True(stored.Active, "Enrichment preserves active state");
                    await reopened.Docks.UpdateAsync(stored, token).ConfigureAwait(false);
                    stored = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Unchanged ownership retained");
                    DatabaseAssert.NotNull(stored.GitAnchorsSnapshot, "Unrelated update retains snapshot");
                    stored.BranchName = "MAIN";
                    await reopened.Docks.UpdateAsync(stored, token).ConfigureAwait(false);
                    stored = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Reused dock retained");
                    DatabaseAssert.True(stored.GitAnchorsSnapshot == null, "Case-only branch reuse clears old evidence on every collation");
                    DatabaseAssert.True(!await reopened.Docks.TryCompleteGitAnchorsAsync(dock.Id, graph.Captain.Id, snapshot, first, token).ConfigureAwait(false), "Old enrichment cannot restore cleared evidence");

                    Dock reclaimed = await fixture.CreateDockAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, graph.Captain.Id,
                        token, configure: value =>
                        {
                            value.GitAnchorsSnapshot = System.Text.Json.JsonSerializer.Deserialize<DockGitAnchorSnapshot>(System.Text.Json.JsonSerializer.Serialize(snapshot))!;
                            value.GitAnchorsSnapshot.DockId = value.Id;
                        }).ConfigureAwait(false);
                    DockGitAnchorSnapshot reclaimSeed = reclaimed.GitAnchorsSnapshot!;
                    DockGitAnchorSnapshot reclaimResult = System.Text.Json.JsonSerializer.Deserialize<DockGitAnchorSnapshot>(System.Text.Json.JsonSerializer.Serialize(first))!;
                    reclaimResult.DockId = reclaimed.Id;
                    reclaimed.Active = false;
                    reclaimed.CaptainId = null;
                    await reopened.Docks.UpdateAsync(reclaimed, token).ConfigureAwait(false);
                    DatabaseAssert.True(!await reopened.Docks.TryCompleteGitAnchorsAsync(reclaimed.Id, graph.Captain.Id, reclaimSeed, reclaimResult, token).ConfigureAwait(false), "Completion after reclaim is rejected");
                    reclaimed = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(reclaimed.Id, token).ConfigureAwait(false), "Reclaimed dock retained");
                    DatabaseAssert.True(!reclaimed.Active && reclaimed.CaptainId == null && reclaimed.GitAnchorsSnapshot == null, "Late enrichment cannot restore active ownership");
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }

        private async Task TestVoyageSummaryAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                UserMaster otherUser = await fixture.CreateUserAsync(graph.Tenant.Id, "summary-other", token: token).ConfigureAwait(false);
                TenantMetadata otherTenant = await fixture.CreateTenantAsync("summary-other", token: token).ConfigureAwait(false);
                UserMaster foreignUser = await fixture.CreateUserAsync(otherTenant.Id, "summary-foreign", token: token).ConfigureAwait(false);
                Vessel second = await fixture.CreateVesselAsync(graph.Tenant.Id, graph.User.Id, graph.Fleet.Id, "summary-second", token).ConfigureAwait(false);
                Vessel hidden = await fixture.CreateVesselAsync(graph.Tenant.Id, otherUser.Id, graph.Fleet.Id, "summary-hidden", token).ConfigureAwait(false);
                string payload = "summary-heavy-payload-" + new string('x', 100000);
                await fixture.CreateMissionAsync(graph.Tenant.Id, graph.User.Id, graph.Voyage.Id, second.Id,
                    graph.Captain.Id, "summary-second", token, configure: mission =>
                    { mission.Status = MissionStatusEnum.Complete; mission.Description = payload; }).ConfigureAwait(false);
                await fixture.CreateMissionAsync(graph.Tenant.Id, graph.User.Id, graph.Voyage.Id, second.Id,
                    graph.Captain.Id, "summary-duplicate-vessel", token, configure: mission => mission.Status = MissionStatusEnum.Complete).ConfigureAwait(false);
                await fixture.CreateMissionAsync(graph.Tenant.Id, otherUser.Id, graph.Voyage.Id, hidden.Id,
                    graph.Captain.Id, "summary-other-user", token, configure: mission => mission.Status = MissionStatusEnum.Failed).ConfigureAwait(false);
                await fixture.CreateMissionAsync(otherTenant.Id, foreignUser.Id, graph.Voyage.Id, hidden.Id,
                    graph.Captain.Id, "summary-other-tenant", token, configure: mission => mission.Status = MissionStatusEnum.Failed).ConfigureAwait(false);

                HashSet<string> visible = new HashSet<string>(StringComparer.Ordinal);
                for (int page = 1; page <= 3; page++)
                {
                    VoyageMissionSummary summary = await _Driver.Missions.ReadVoyageMissionSummaryAsync(
                        graph.Voyage.Id, page, 1, graph.Tenant.Id, graph.User.Id, token).ConfigureAwait(false);
                    DatabaseAssert.EnumerationPage(summary.Vessels, page, 1, 2, 2, page <= 2 ? 1 : 0);
                    DatabaseAssert.Equal(2L, summary.StatusCounts[MissionStatusEnum.Complete], "Counts include missions beyond vessel page");
                    DatabaseAssert.Equal(3L, System.Linq.Enumerable.Sum(summary.StatusCounts.Values), "User-visible total");
                    foreach (string id in summary.Vessels.Objects) DatabaseAssert.True(visible.Add(id), "No duplicate vessel across pages");
                    DatabaseAssert.True(!System.Text.Json.JsonSerializer.Serialize(summary).Contains("summary-heavy-payload"), "Summary omits mission payload");
                }
                DatabaseAssert.ContainsIds(visible, id => id, graph.Vessel.Id, second.Id);
                VoyageMissionSummary tenant = await _Driver.Missions.ReadVoyageMissionSummaryAsync(
                    graph.Voyage.Id, tenantId: graph.Tenant.Id, token: token).ConfigureAwait(false);
                DatabaseAssert.Equal(4L, System.Linq.Enumerable.Sum(tenant.StatusCounts.Values), "Tenant-visible total");
                DatabaseAssert.Equal(3L, tenant.Vessels.TotalRecords, "Tenant distinct vessels");
                VoyageMissionSummary global = await _Driver.Missions.ReadVoyageMissionSummaryAsync(graph.Voyage.Id, token: token).ConfigureAwait(false);
                DatabaseAssert.Equal(5L, System.Linq.Enumerable.Sum(global.StatusCounts.Values), "Global total");
                VoyageMissionSummary absent = await _Driver.Missions.ReadVoyageMissionSummaryAsync(
                    "absent-" + Guid.NewGuid().ToString("N"), token: token).ConfigureAwait(false);
                DatabaseAssert.Equal(0, absent.StatusCounts.Count, "Empty voyage counts");
                DatabaseAssert.Equal(0L, absent.Vessels.TotalRecords, "Empty voyage associations");
                foreach (Func<Task<VoyageMissionSummary>> invalid in new Func<Task<VoyageMissionSummary>>[]
                {
                    () => _Driver.Missions.ReadVoyageMissionSummaryAsync(graph.Voyage.Id, pageNumber: 0, token: token),
                    () => _Driver.Missions.ReadVoyageMissionSummaryAsync(graph.Voyage.Id, pageSize: 101, token: token),
                    () => _Driver.Missions.ReadVoyageMissionSummaryAsync(graph.Voyage.Id, tenantId: "", token: token),
                    () => _Driver.Missions.ReadVoyageMissionSummaryAsync(graph.Voyage.Id, userId: graph.User.Id, token: token)
                })
                {
                    bool rejected = false;
                    try { await invalid().ConfigureAwait(false); }
                    catch (ArgumentException) { rejected = true; }
                    DatabaseAssert.True(rejected, "Invalid scope or page must be rejected");
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }

        private async Task TestVoyageCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                UserMaster user = graph.User;
                Voyage voyage = graph.Voyage;
                Voyage? read = await _Driver.Voyages.ReadAsync(voyage.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Voyage read returned null");
                DatabaseAssert.Equal(user.Id, read.UserId, "Voyage.UserId");
                DatabaseAssert.Equal(voyage.Title, read.Title, "Voyage.Title");

                read.Status = VoyageStatusEnum.Complete;
                Voyage updated = await _Driver.Voyages.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(VoyageStatusEnum.Complete, updated.Status, "Voyage.Status");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestMissionCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Vessel vessel = graph.Vessel;
                Captain captain = graph.Captain;
                Voyage voyage = graph.Voyage;
                Mission mission = graph.Mission;
                Mission? read = await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Mission read returned null");
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "Mission.VesselId");
                DatabaseAssert.Equal(captain.Id, read.CaptainId, "Mission.CaptainId");
                DatabaseAssert.Equal(voyage.Id, read.VoyageId, "Mission.VoyageId");

                DateTime startedUtc = DateTime.UtcNow.AddMinutes(-5);
                DateTime completedUtc = startedUtc.AddMilliseconds(2500);
                Mission timedMission = await fixture.CreateMissionAsync(
                    mission.TenantId,
                    mission.UserId,
                    voyage.Id,
                    vessel.Id,
                    captain.Id,
                    "timed-mission",
                    token: token,
                    startedUtc: startedUtc,
                    completedUtc: completedUtc).ConfigureAwait(false);
                Mission? timedRead = await _Driver.Missions.ReadAsync(timedMission.Id, token).ConfigureAwait(false);
                timedRead = DatabaseAssert.NotNull(timedRead, "Timed mission read returned null");
                DatabaseAssert.Equal(2500L, timedRead.TotalRuntimeMs ?? -1L, "Mission.TotalRuntimeMs");

                read.Status = MissionStatusEnum.InProgress;
                read.Priority = 3;
                read.StartedUtc = startedUtc;
                read.CompletedUtc = completedUtc;
                Mission updated = await _Driver.Missions.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(MissionStatusEnum.InProgress, updated.Status, "Mission.Status");
                DatabaseAssert.Equal(3, updated.Priority, "Mission.Priority");
                DatabaseAssert.Equal(2500L, updated.TotalRuntimeMs ?? -1L, "Updated Mission.TotalRuntimeMs");
                Mission? updatedRead = await _Driver.Missions.ReadAsync(read.Id, token).ConfigureAwait(false);
                updatedRead = DatabaseAssert.NotNull(updatedRead, "Updated mission read returned null");
                DatabaseAssert.Equal(2500L, updatedRead.TotalRuntimeMs ?? -1L, "Persisted Mission.TotalRuntimeMs");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestMissionForkFieldsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            Mission? preserved = null;
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                preserved = new Mission("Preserved mission fields")
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    VoyageId = graph.Voyage.Id,
                    CaptainId = graph.Captain.Id,
                    DependsOnMissionId = graph.Mission.Id,
                    StageOrder = 3,
                    AssignmentState = MissionAssignmentStateEnum.WaitingForProviderUsage,
                    ProcessId = 12345,
                    Persona = "Judge",
                    PreferredModel = "high",
                    CapabilityHint = "review",
                    Mode = MissionModeEnum.Audit,
                    RequiresReview = true,
                    ReviewDenyAction = ReviewDenyActionEnum.FailPipeline,
                    ReviewComment = "Review evidence",
                    ReviewedByUserId = graph.User.Id,
                    ReviewRequestedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    ReviewedUtc = new DateTime(2026, 1, 2, 3, 5, 5, DateTimeKind.Utc),
                    PrestagedFiles = new List<PrestagedFile> { PrestagedFile.FromContent("input.txt", "preserved input") },
                    RecoveryAttempts = 2,
                    LandingRetryCount = 3,
                    StartFromRef = "refs/heads/accepted-source",
                    LastRecoveryActionUtc = new DateTime(2026, 1, 2, 3, 6, 5, DateTimeKind.Utc),
                    RetrySkipCaptainIds = graph.Captain.Id
                };
                await _Driver.Missions.CreateAsync(preserved, token).ConfigureAwait(false);
                await AssertReopenedMissionAsync(preserved, token).ConfigureAwait(false);

                preserved.StageOrder = 7;
                preserved.AssignmentState = MissionAssignmentStateEnum.WaitingForVesselMutex;
                preserved.ProcessId = 23456;
                preserved.Persona = "Worker";
                preserved.PreferredModel = "mid";
                preserved.CapabilityHint = "implementation";
                preserved.Mode = MissionModeEnum.Research;
                preserved.RequiresReview = false;
                preserved.ReviewDenyAction = ReviewDenyActionEnum.RetryStage;
                preserved.ReviewedByUserId = null;
                preserved.ReviewRequestedUtc = preserved.ReviewRequestedUtc.Value.AddDays(1);
                preserved.ReviewedUtc = preserved.ReviewedUtc.Value.AddDays(1);
                preserved.LastRecoveryActionUtc = preserved.LastRecoveryActionUtc.Value.AddDays(1);
                preserved.ReviewComment = "Updated review evidence";
                preserved.PrestagedFiles[0].Content = "updated input";
                preserved.RecoveryAttempts = 4;
                preserved.LandingRetryCount = 5;
                preserved.StartFromRef = "refs/heads/updated-source";
                preserved.RetrySkipCaptainIds = null;
                await _Driver.Missions.UpdateAsync(preserved, token).ConfigureAwait(false);
                await AssertReopenedMissionAsync(preserved, token).ConfigureAwait(false);
            }
            finally
            {
                if (!_NoCleanup && preserved != null)
                    await _Driver.Missions.DeleteAsync(preserved.Id, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task AssertReopenedMissionAsync(Mission expected, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Mission read = DatabaseAssert.NotNull(await reopened.Missions.ReadAsync(expected.TenantId, expected.UserId, expected.Id, token).ConfigureAwait(false), "Reopened mission missing");
                AssertMissionForkFields(expected, read);
                EnumerationResult<Mission> page = await reopened.Missions.EnumerateAsync(expected.TenantId, expected.UserId,
                    new EnumerationQuery { PageSize = 100 }, token).ConfigureAwait(false);
                Mission queried = page.Objects.Find(item => item.Id == expected.Id);
                AssertMissionForkFields(expected, DatabaseAssert.NotNull(queried, "Queried mission missing"));
                DatabaseAssert.True(await reopened.Missions.ReadAsync("other-tenant", expected.UserId, expected.Id, token).ConfigureAwait(false) == null,
                    "Cross-tenant mission read must be denied");
            }
        }

        private static void AssertMissionForkFields(Mission expected, Mission actual)
        {
            DatabaseAssert.Equal(expected.TenantId, actual.TenantId, "Mission.TenantId");
            DatabaseAssert.Equal(expected.UserId, actual.UserId, "Mission.UserId");
            DatabaseAssert.Equal(expected.CaptainId, actual.CaptainId, "Mission.CaptainId");
            DatabaseAssert.Equal(expected.DependsOnMissionId, actual.DependsOnMissionId, "Mission.DependsOnMissionId");
            DatabaseAssert.Equal(expected.StageOrder, actual.StageOrder, "Mission.StageOrder");
            DatabaseAssert.Equal(expected.AssignmentState, actual.AssignmentState, "Mission.AssignmentState");
            DatabaseAssert.Equal(expected.ProcessId, actual.ProcessId, "Mission.ProcessId");
            DatabaseAssert.Equal(expected.Persona, actual.Persona, "Mission.Persona");
            DatabaseAssert.Equal(expected.PreferredModel, actual.PreferredModel, "Mission.PreferredModel");
            DatabaseAssert.Equal(expected.CapabilityHint, actual.CapabilityHint, "Mission.CapabilityHint");
            DatabaseAssert.Equal(expected.Mode, actual.Mode, "Mission.Mode");
            DatabaseAssert.Equal(expected.RequiresReview, actual.RequiresReview, "Mission.RequiresReview");
            DatabaseAssert.Equal(expected.ReviewDenyAction, actual.ReviewDenyAction, "Mission.ReviewDenyAction");
            DatabaseAssert.Equal(expected.ReviewComment, actual.ReviewComment, "Mission.ReviewComment");
            DatabaseAssert.Equal(expected.ReviewedByUserId, actual.ReviewedByUserId, "Mission.ReviewedByUserId");
            DatabaseAssert.Equal(expected.ReviewRequestedUtc, actual.ReviewRequestedUtc, "Mission.ReviewRequestedUtc");
            DatabaseAssert.Equal(expected.ReviewedUtc, actual.ReviewedUtc, "Mission.ReviewedUtc");
            DatabaseAssert.Equal(expected.PrestagedFiles.Count, actual.PrestagedFiles?.Count ?? 0, "Mission.PrestagedFiles.Count");
            DatabaseAssert.Equal(expected.PrestagedFiles[0].Content, actual.PrestagedFiles[0].Content, "Mission.PrestagedFiles.Content");
            DatabaseAssert.Equal(expected.RecoveryAttempts, actual.RecoveryAttempts, "Mission.RecoveryAttempts");
            DatabaseAssert.Equal(expected.LandingRetryCount, actual.LandingRetryCount, "Mission.LandingRetryCount");
            DatabaseAssert.Equal(expected.StartFromRef, actual.StartFromRef, "Mission.StartFromRef");
            DatabaseAssert.Equal(expected.LastRecoveryActionUtc, actual.LastRecoveryActionUtc, "Mission.LastRecoveryActionUtc");
            DatabaseAssert.Equal(expected.RetrySkipCaptainIds, actual.RetrySkipCaptainIds, "Mission.RetrySkipCaptainIds");
        }

        private async Task TestDockCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Vessel vessel = graph.Vessel;
                Captain captain = graph.Captain;
                Dock dock = graph.Dock;
                Dock? read = await _Driver.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Dock read returned null");
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "Dock.VesselId");
                DatabaseAssert.Equal(captain.Id, read.CaptainId, "Dock.CaptainId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "Dock.UserId survives create/read");

                // The tenant and user lists read newest first; a tenant delete walks the tenant list.
                Dock newer = await fixture.CreateDockAsync(graph.Tenant.Id, graph.User.Id, vessel.Id, captain.Id, token,
                    item => item.CreatedUtc = dock.CreatedUtc.AddSeconds(5)).ConfigureAwait(false);
                List<Dock> tenantDocks = await _Driver.Docks.EnumerateAsync(graph.Tenant.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(newer.Id + "," + dock.Id, String.Join(",", tenantDocks.ConvertAll(item => item.Id)), "Tenant docks, newest first");
                List<Dock> userDocks = await _Driver.Docks.EnumerateAsync(graph.Tenant.Id, graph.User.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(newer.Id + "," + dock.Id, String.Join(",", userDocks.ConvertAll(item => item.Id)), "User docks, newest first");

                read.Active = false;
                UserMaster other = await fixture.CreateUserAsync(graph.Tenant.Id, "dock-owner-update", token: token).ConfigureAwait(false);
                read.UserId = other.Id;
                Dock updated = await _Driver.Docks.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(false, updated.Active, "Dock.Active");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Dock persisted = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Updated dock retained");
                    DatabaseAssert.Equal(other.Id, persisted.UserId, "Dock.UserId update survives reopen");
                    persisted.UserId = null;
                    await reopened.Docks.UpdateAsync(persisted, token).ConfigureAwait(false);
                }
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                    DatabaseAssert.True((await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false))!.UserId == null, "Dock.UserId can be cleared");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestSignalCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                TenantMetadata tenant = graph.Tenant;
                Captain captain = graph.Captain;
                Signal signal = graph.Signal;
                Signal? read = await _Driver.Signals.ReadAsync(signal.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Signal read returned null");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "Signal.TenantId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "Signal.UserId");
                DatabaseAssert.Equal(captain.Id, read.ToCaptainId, "Signal.ToCaptainId");
                DatabaseAssert.Equal(false, read.Read, "Signal.Read");

                EnumerationResult<Signal> page = await _Driver.Signals.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 10 }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 1, "Signal enumeration should include created signal");
                DatabaseAssert.ContainsIds(page.Objects, x => x.Id, signal.Id);
                DatabaseAssert.Equal(graph.User.Id, page.Objects.Find(x => x.Id == signal.Id)!.UserId, "Enumerated Signal.UserId");

                await _Driver.Signals.MarkReadAsync(signal.Id, token).ConfigureAwait(false);
                Signal? reread = await _Driver.Signals.ReadAsync(signal.Id, token).ConfigureAwait(false);
                reread = DatabaseAssert.NotNull(reread, "Signal re-read returned null");
                DatabaseAssert.Equal(true, reread.Read, "Signal.Read after mark read");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestSignalLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Captain captain = graph.Captain;
                Signal signalA = graph.Signal;
                Signal signalB = await fixture.CreateSignalAsync(signalA.TenantId!, signalA.UserId!, captain.Id, token).ConfigureAwait(false);

                List<Signal> recent = await _Driver.Signals.EnumerateRecentAsync(10, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(recent, x => x.Id, signalA.Id, signalB.Id);

                List<Signal> unread = await _Driver.Signals.EnumerateByRecipientAsync(captain.Id, true, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(unread, x => x.Id, signalA.Id, signalB.Id);

                await _Driver.Signals.MarkReadAsync(signalA.TenantId!, signalA.Id, token).ConfigureAwait(false);
                List<Signal> unreadAfter = await _Driver.Signals.EnumerateByRecipientAsync(captain.Id, true, token).ConfigureAwait(false);
                DatabaseAssert.True(!unreadAfter.Exists(x => x.Id == signalA.Id), "Tenant-scoped MarkReadAsync should remove signal from unread list");
                DatabaseAssert.True(unreadAfter.Exists(x => x.Id == signalB.Id), "Unread list should still contain untouched signal");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestEventCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                TenantMetadata tenant = graph.Tenant;
                Vessel vessel = graph.Vessel;
                Captain captain = graph.Captain;
                Voyage voyage = graph.Voyage;
                Mission mission = graph.Mission;
                ArmadaEvent evt = graph.Event;
                ArmadaEvent? read = await _Driver.Events.ReadAsync(evt.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Event read returned null");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "Event.TenantId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "Event.UserId");
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "Event.VesselId");
                DatabaseAssert.Equal(captain.Id, read.CaptainId, "Event.CaptainId");
                DatabaseAssert.Equal(mission.Id, read.MissionId, "Event.MissionId");
                DatabaseAssert.Equal(voyage.Id, read.VoyageId, "Event.VoyageId");

                EnumerationResult<ArmadaEvent> page = await _Driver.Events.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 10, MissionId = mission.Id }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 1, "Event enumeration should include created event");
                DatabaseAssert.ContainsIds(page.Objects, x => x.Id, evt.Id);
                DatabaseAssert.Equal(graph.User.Id, page.Objects.Find(x => x.Id == evt.Id)!.UserId, "Enumerated Event.UserId");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestEventLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            ArmadaEvent? evtB = null;
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                Vessel vessel = graph.Vessel;
                Captain captain = graph.Captain;
                Voyage voyage = graph.Voyage;
                Mission mission = graph.Mission;
                ArmadaEvent evtA = graph.Event;
                evtB = new ArmadaEvent("mission.updated", "Mission updated for integration test")
                {
                    TenantId = evtA.TenantId,
                    UserId = evtA.UserId,
                    MissionId = mission.Id,
                    VoyageId = voyage.Id,
                    VesselId = vessel.Id,
                    CaptainId = captain.Id,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    Payload = "{\"kind\":\"test-update\"}"
                };
                evtB = await _Driver.Events.CreateAsync(evtB, token).ConfigureAwait(false);

                List<ArmadaEvent> recent = await _Driver.Events.EnumerateRecentAsync(10, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(recent, x => x.Id, evtA.Id, evtB.Id);

                List<ArmadaEvent> byType = await _Driver.Events.EnumerateByTypeAsync("mission.created", 10, token).ConfigureAwait(false);
                DatabaseAssert.True(byType.Exists(x => x.Id == evtA.Id), "EnumerateByTypeAsync should contain mission.created event");

                DatabaseAssert.ContainsIds(await _Driver.Events.EnumerateByEntityAsync("mission", mission.Id, 10, token).ConfigureAwait(false), x => x.Id, evtA.Id, evtB.Id);
                DatabaseAssert.ContainsIds(await _Driver.Events.EnumerateByCaptainAsync(captain.Id, 10, token).ConfigureAwait(false), x => x.Id, evtA.Id, evtB.Id);
                DatabaseAssert.ContainsIds(await _Driver.Events.EnumerateByMissionAsync(mission.Id, 10, token).ConfigureAwait(false), x => x.Id, evtA.Id, evtB.Id);
                DatabaseAssert.ContainsIds(await _Driver.Events.EnumerateByVesselAsync(vessel.Id, 10, token).ConfigureAwait(false), x => x.Id, evtA.Id, evtB.Id);
                DatabaseAssert.ContainsIds(await _Driver.Events.EnumerateByVoyageAsync(voyage.Id, 10, token).ConfigureAwait(false), x => x.Id, evtA.Id, evtB.Id);
            }
            finally
            {
                if (!_NoCleanup && evtB != null)
                {
                    try
                    {
                        await _Driver.Events.DeleteAsync(evtB.Id, token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestMergeEntryCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                TenantMetadata tenant = graph.Tenant;
                UserMaster user = graph.User;
                Vessel vessel = graph.Vessel;
                Mission mission = graph.Mission;
                MergeEntry merge = graph.MergeEntry;
                MergeEntry? read = await _Driver.MergeEntries.ReadAsync(merge.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Merge entry read returned null");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "MergeEntry.TenantId");
                DatabaseAssert.Equal(user.Id, read.UserId, "MergeEntry.UserId");
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "MergeEntry.VesselId");
                DatabaseAssert.Equal(mission.Id, read.MissionId, "MergeEntry.MissionId");

                DatabaseAssert.Equal(merge.Status, read.Status, "MergeEntry.Status");
                AssertMergeAuditFields(new MergeEntry(), read, "created without audit values");

                // Every audit value the writes persist must come back on a read, and a later write that
                // does not touch them must not replace them with what a partial read returned.
                MergeEntry audited = new MergeEntry("feature/audited-" + Guid.NewGuid().ToString("N"))
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    MissionId = mission.Id,
                    VesselId = vessel.Id,
                    Status = MergeStatusEnum.Queued
                };
                SetMergeAuditFields(audited, "created");
                await _Driver.MergeEntries.CreateAsync(audited, token).ConfigureAwait(false);
                try
                {
                    MergeEntry createdRead = DatabaseAssert.NotNull(await _Driver.MergeEntries.ReadAsync(audited.Id, token).ConfigureAwait(false), "Audited merge entry read after create");
                    AssertMergeAuditFields(audited, createdRead, "after create");
                }
                finally
                {
                    if (!_NoCleanup) await _Driver.MergeEntries.DeleteAsync(audited.Id, token).ConfigureAwait(false);
                }

                read.Status = MergeStatusEnum.Landed;
                SetMergeAuditFields(read, "updated");
                await _Driver.MergeEntries.UpdateAsync(read, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    MergeEntry persisted = DatabaseAssert.NotNull(await reopened.MergeEntries.ReadAsync(merge.Id, token).ConfigureAwait(false), "Updated merge entry retained");
                    DatabaseAssert.Equal(MergeStatusEnum.Landed, persisted.Status, "MergeEntry.Status after update and reopen");
                    AssertMergeAuditFields(read, persisted, "after update and reopen");

                    persisted.Priority = persisted.Priority + 1;
                    await reopened.MergeEntries.UpdateAsync(persisted, token).ConfigureAwait(false);
                }
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    MergeEntry rewritten = DatabaseAssert.NotNull(await reopened.MergeEntries.ReadAsync(merge.Id, token).ConfigureAwait(false), "Rewritten merge entry retained");
                    DatabaseAssert.Equal(MergeStatusEnum.Landed, rewritten.Status, "MergeEntry.Status after an unrelated update");
                    AssertMergeAuditFields(read, rewritten, "after an unrelated update");
                }

                EnumerationResult<MergeEntry> page = await _Driver.MergeEntries.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 10, MissionId = mission.Id }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 1, "Merge entry enumeration should include created merge entry");
                DatabaseAssert.ContainsIds(page.Objects, x => x.Id, merge.Id);
                AssertMergeAuditFields(read, page.Objects.Find(x => x.Id == merge.Id)!, "enumerated");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private static void SetMergeAuditFields(MergeEntry entry, string label)
        {
            bool created = label == "created";
            entry.AuditLane = created ? "Deferred" : "Fast";
            entry.AuditConventionPassed = !created;
            entry.AuditConventionNotes = "convention notes " + label;
            entry.AuditCriticalTrigger = "critical trigger " + label;
            entry.AuditDeepPicked = created;
            entry.AuditDeepCompletedUtc = created
                ? new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)
                : new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            entry.AuditDeepVerdict = created ? "Pass" : "Concern";
            entry.AuditDeepNotes = "deep notes " + label;
            entry.AuditDeepRecommendedAction = "recommended action " + label;
        }

        private static void AssertMergeAuditFields(MergeEntry expected, MergeEntry actual, string stage)
        {
            DatabaseAssert.Equal(expected.AuditLane, actual.AuditLane, "MergeEntry.AuditLane " + stage);
            DatabaseAssert.Equal(expected.AuditConventionPassed, actual.AuditConventionPassed, "MergeEntry.AuditConventionPassed " + stage);
            DatabaseAssert.Equal(expected.AuditConventionNotes, actual.AuditConventionNotes, "MergeEntry.AuditConventionNotes " + stage);
            DatabaseAssert.Equal(expected.AuditCriticalTrigger, actual.AuditCriticalTrigger, "MergeEntry.AuditCriticalTrigger " + stage);
            DatabaseAssert.Equal(expected.AuditDeepPicked, actual.AuditDeepPicked, "MergeEntry.AuditDeepPicked " + stage);
            DatabaseAssert.Equal(expected.AuditDeepCompletedUtc, actual.AuditDeepCompletedUtc, "MergeEntry.AuditDeepCompletedUtc " + stage);
            DatabaseAssert.Equal(expected.AuditDeepVerdict, actual.AuditDeepVerdict, "MergeEntry.AuditDeepVerdict " + stage);
            DatabaseAssert.Equal(expected.AuditDeepNotes, actual.AuditDeepNotes, "MergeEntry.AuditDeepNotes " + stage);
            DatabaseAssert.Equal(expected.AuditDeepRecommendedAction, actual.AuditDeepRecommendedAction, "MergeEntry.AuditDeepRecommendedAction " + stage);
        }

        private async Task TestMergeEntryLookupAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                MergeEntry merge = graph.MergeEntry;
                DatabaseAssert.True(await _Driver.MergeEntries.ExistsAsync(merge.Id, token).ConfigureAwait(false), "MergeEntry ExistsAsync should return true");

                List<MergeEntry> queued = await _Driver.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.Queued, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(queued, x => x.Id, merge.Id);

                List<MergeEntry> all = await _Driver.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(all, x => x.Id, merge.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestWorkflowProfileCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("workflow-profile-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "workflow-profile-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "workflow-profile-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "workflow-profile-vessel", token).ConfigureAwait(false);
                WorkflowProfile profileA = await fixture.CreateWorkflowProfileAsync(tenant.Id, user.Id, "workflow-profile-a", fleet.Id, vessel.Id, token).ConfigureAwait(false);
                WorkflowProfile profileB = await fixture.CreateWorkflowProfileAsync(tenant.Id, user.Id, "workflow-profile-b", fleet.Id, null, token).ConfigureAwait(false);

                WorkflowProfile? read = await _Driver.WorkflowProfiles.ReadAsync(profileA.Id, null, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Workflow profile read returned null");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "WorkflowProfile.TenantId");
                DatabaseAssert.Equal(user.Id, read.UserId, "WorkflowProfile.UserId");
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "WorkflowProfile.VesselId");
                DatabaseAssert.Equal(WorkflowProfileScopeEnum.Vessel, read.Scope, "WorkflowProfile.Scope");
                DatabaseAssert.Equal("API_TOKEN", read.RequiredInputs[0].Key, "WorkflowProfile.RequiredInputs[0].Key");
                DatabaseAssert.Equal("staging", read.Environments[0].EnvironmentName, "WorkflowProfile.Environments[0].EnvironmentName");

                read.Description = "Updated workflow profile description";
                read.BuildCommand = "dotnet build -c Release";
                read.ExpectedArtifacts.Add("artifacts/extra.zip");
                List<string> expectedArtifacts = new List<string>(read.ExpectedArtifacts);
                await _Driver.WorkflowProfiles.UpdateAsync(read, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    WorkflowProfile persisted = DatabaseAssert.NotNull(await reopened.WorkflowProfiles.ReadAsync(profileA.Id, null, token).ConfigureAwait(false), "Updated workflow profile retained");
                    DatabaseAssert.Equal("Updated workflow profile description", persisted.Description, "Updated WorkflowProfile.Description after reopen");
                    DatabaseAssert.Equal("dotnet build -c Release", persisted.BuildCommand, "Updated WorkflowProfile.BuildCommand after reopen");
                    DatabaseAssert.Equal(String.Join("|", expectedArtifacts), String.Join("|", persisted.ExpectedArtifacts), "Updated WorkflowProfile.ExpectedArtifacts after reopen");
                }

                EnumerationResult<WorkflowProfile> page = await _Driver.WorkflowProfiles.EnumerateAsync(new WorkflowProfileQuery
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    PageNumber = 1,
                    PageSize = 10
                }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 2, "Workflow profile enumeration should include created profiles");
                DatabaseAssert.ContainsIds(page.Objects, item => item.Id, profileA.Id, profileB.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestPipelineWriteAtomicityAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string triggerName = "trg_block_pipeline_delete_" + suffix;
            Pipeline pipeline = new Pipeline("atomic-pipeline-" + suffix) { Description = "original description" };
            bool triggerInstalled = false;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("pipeline-atomic-tenant", token: token).ConfigureAwait(false);
                pipeline.TenantId = tenant.Id;
                pipeline.Stages.Add(new PipelineStage(1, "Worker"));
                pipeline.Stages.Add(new PipelineStage(2, "Judge"));
                await _Driver.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);

                // A stage insert that fails after the parent row and the old stages were rewritten (here a
                // duplicate stage id) must leave the stored pipeline exactly as it was.
                Pipeline changed = DatabaseAssert.NotNull(await _Driver.Pipelines.ReadAsync(pipeline.Id, token).ConfigureAwait(false), "Pipeline read after create");
                changed.Description = "changed description";
                PipelineStage architect = new PipelineStage(1, "Architect");
                PipelineStage duplicate = new PipelineStage(2, "Worker") { Id = architect.Id };
                changed.Stages = new List<PipelineStage> { architect, duplicate };
                bool updateFailed = false;
                try { await _Driver.Pipelines.UpdateAsync(changed, token).ConfigureAwait(false); }
                catch (Exception) { updateFailed = true; }
                DatabaseAssert.True(updateFailed, "An update whose stage insert fails must throw");
                await AssertPipelineUnchangedAsync(pipeline.Id, "original description", "after a failed update", token).ConfigureAwait(false);

                // A parent delete that fails after the stages were deleted must leave the stages in place.
                await ExecuteRawAsync(BlockPipelineDeleteSql(triggerName, pipeline.Id), token).ConfigureAwait(false);
                triggerInstalled = true;
                bool deleteFailed = false;
                try { await _Driver.Pipelines.DeleteAsync(pipeline.Id, token).ConfigureAwait(false); }
                catch (Exception) { deleteFailed = true; }
                DatabaseAssert.True(deleteFailed, "A delete whose parent delete fails must throw");
                await DropPipelineDeleteBlockAsync(triggerName, token).ConfigureAwait(false);
                triggerInstalled = false;
                await AssertPipelineUnchangedAsync(pipeline.Id, "original description", "after a failed delete", token).ConfigureAwait(false);

                await _Driver.Pipelines.DeleteAsync(pipeline.Id, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                    DatabaseAssert.True(await reopened.Pipelines.ReadAsync(pipeline.Id, token).ConfigureAwait(false) == null, "Pipeline removed by a successful delete");
                DatabaseAssert.Equal(0L, await CountPipelineStagesAsync(pipeline.Id, token).ConfigureAwait(false), "Stages removed by a successful delete");
            }
            finally
            {
                if (triggerInstalled) await DropPipelineDeleteBlockAsync(triggerName, token).ConfigureAwait(false);
                if (!_NoCleanup)
                {
                    try { await _Driver.Pipelines.DeleteAsync(pipeline.Id, token).ConfigureAwait(false); }
                    catch (Exception ex) { Console.WriteLine("  pipeline cleanup failed: " + ex.Message); }
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestPipelineSiblingOrderAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            Pipeline pipeline = new Pipeline("sibling-pipeline-" + suffix);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("pipeline-sibling-tenant", token: token).ConfigureAwait(false);
                pipeline.TenantId = tenant.Id;

                // The stage ids sort opposite to the submitted order, so a read that orders ties by key or by
                // insertion luck cannot pass: only the stored submitted position gives this order back.
                pipeline.Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Worker") { Id = "pps_" + suffix + "_e" },
                    new PipelineStage(2, "TestEngineer") { Id = "pps_" + suffix + "_d" },
                    new PipelineStage(2, "Linter") { Id = "pps_" + suffix + "_c" },
                    new PipelineStage(2, "Reviewer") { Id = "pps_" + suffix + "_b" },
                    new PipelineStage(3, "Judge") { Id = "pps_" + suffix + "_a" }
                };
                await _Driver.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);
                await AssertStageOrderAsync(pipeline.Id, "Worker#1|TestEngineer#2|Linter#2|Reviewer#2|Judge#3", "after create", token).ConfigureAwait(false);

                Pipeline changed = DatabaseAssert.NotNull(await _Driver.Pipelines.ReadAsync(pipeline.Id, token).ConfigureAwait(false), "Pipeline read after create");
                changed.Stages = new List<PipelineStage>
                {
                    new PipelineStage(1, "Reviewer") { Id = "pps_" + suffix + "_z" },
                    new PipelineStage(1, "Worker") { Id = "pps_" + suffix + "_y" },
                    new PipelineStage(2, "Judge") { Id = "pps_" + suffix + "_x" }
                };
                await _Driver.Pipelines.UpdateAsync(changed, token).ConfigureAwait(false);
                await AssertStageOrderAsync(pipeline.Id, "Reviewer#1|Worker#1|Judge#2", "after update", token).ConfigureAwait(false);
            }
            finally
            {
                if (!_NoCleanup)
                {
                    try { await _Driver.Pipelines.DeleteAsync(pipeline.Id, token).ConfigureAwait(false); }
                    catch (Exception ex) { Console.WriteLine("  pipeline cleanup failed: " + ex.Message); }
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task AssertStageOrderAsync(string pipelineId, string expected, string stage, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Pipeline stored = DatabaseAssert.NotNull(await reopened.Pipelines.ReadAsync(pipelineId, token).ConfigureAwait(false), "Pipeline retained " + stage);
                DatabaseAssert.Equal(expected, String.Join("|", stored.Stages.ConvertAll(item => item.PersonaName + "#" + item.Order)), "Stored stage order " + stage);
            }
        }

        private async Task AssertPipelineUnchangedAsync(string pipelineId, string description, string stage, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Pipeline stored = DatabaseAssert.NotNull(await reopened.Pipelines.ReadAsync(pipelineId, token).ConfigureAwait(false), "Pipeline retained " + stage);
                DatabaseAssert.Equal(description, stored.Description, "Pipeline.Description " + stage);
                DatabaseAssert.Equal("Worker|Judge", String.Join("|", stored.Stages.ConvertAll(item => item.PersonaName)), "Pipeline stages " + stage);
            }
        }

        private async Task<long> CountPipelineStagesAsync(string pipelineId, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM pipeline_stages WHERE pipeline_id = @id;";
                    DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "@id";
                    parameter.Value = pipelineId;
                    command.Parameters.Add(parameter);
                    return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
                }
            }
        }

        private List<string> BlockPipelineDeleteSql(string triggerName, string pipelineId)
        {
            switch (_Settings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return new List<string> { "CREATE TRIGGER " + triggerName + " BEFORE DELETE ON pipelines WHEN OLD.id = '" + pipelineId + "' BEGIN SELECT RAISE(ABORT, 'pipeline delete blocked'); END;" };
                case DatabaseTypeEnum.Postgresql:
                    return new List<string>
                    {
                        "CREATE FUNCTION " + triggerName + "_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'pipeline delete blocked'; END $$;",
                        "CREATE TRIGGER " + triggerName + " BEFORE DELETE ON pipelines FOR EACH ROW WHEN (OLD.id = '" + pipelineId + "') EXECUTE FUNCTION " + triggerName + "_fn();"
                    };
                case DatabaseTypeEnum.Mysql:
                    return new List<string> { "CREATE TRIGGER " + triggerName + " BEFORE DELETE ON pipelines FOR EACH ROW BEGIN IF OLD.id = '" + pipelineId + "' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'pipeline delete blocked'; END IF; END" };
                case DatabaseTypeEnum.SqlServer:
                    return new List<string> { "CREATE TRIGGER " + triggerName + " ON pipelines AFTER DELETE AS BEGIN IF EXISTS (SELECT 1 FROM deleted WHERE id = '" + pipelineId + "') THROW 50001, 'pipeline delete blocked', 1; END" };
                default:
                    throw new NotSupportedException("No delete block for provider " + _Settings.Type);
            }
        }

        private async Task DropPipelineDeleteBlockAsync(string triggerName, CancellationToken token)
        {
            List<string> statements = _Settings.Type switch
            {
                DatabaseTypeEnum.Postgresql => new List<string> { "DROP TRIGGER IF EXISTS " + triggerName + " ON pipelines;", "DROP FUNCTION IF EXISTS " + triggerName + "_fn();" },
                DatabaseTypeEnum.SqlServer => new List<string> { "DROP TRIGGER IF EXISTS " + triggerName + ";" },
                _ => new List<string> { "DROP TRIGGER IF EXISTS " + triggerName + ";" }
            };
            await ExecuteRawAsync(statements, token).ConfigureAwait(false);
        }

        private async Task ExecuteRawAsync(List<string> statements, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                foreach (string sql in statements)
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = sql;
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task TestRequestHistorySameDayRangeAsync(CancellationToken token)
        {
            string route = "/api/v1/range-probe/" + Guid.NewGuid().ToString("N");
            DateTime day = DateTime.UtcNow.Date.AddDays(-1);
            RequestHistoryEntry morning = new RequestHistoryEntry { Method = "GET", Route = route, CreatedUtc = day.AddHours(10).AddMilliseconds(123) };
            RequestHistoryEntry afternoon = new RequestHistoryEntry { Method = "GET", Route = route, CreatedUtc = day.AddHours(14) };
            await _Driver.RequestHistory.CreateAsync(morning, null, token).ConfigureAwait(false);
            await _Driver.RequestHistory.CreateAsync(afternoon, null, token).ConfigureAwait(false);
            try
            {
                Console.WriteLine("  stored request_history.created_utc: " + await ReadRawRequestCreatedAsync(morning.Id, token).ConfigureAwait(false));
                RequestHistoryRecord stored = DatabaseAssert.NotNull(await _Driver.RequestHistory.ReadAsync(morning.Id, null, token).ConfigureAwait(false), "Request history read after create");
                DatabaseAssert.True(Math.Abs((stored.Entry.CreatedUtc - morning.CreatedUtc).TotalMilliseconds) < 1,
                    "RequestHistory.CreatedUtc round trip: expected " + morning.CreatedUtc.ToString("o") + " got " + stored.Entry.CreatedUtc.ToString("o"));
                DatabaseAssert.Equal(DateTimeKind.Utc, stored.Entry.CreatedUtc.Kind, "RequestHistory.CreatedUtc kind");

                DateTime noon = day.AddHours(12);
                EnumerationResult<RequestHistoryEntry> after = await _Driver.RequestHistory.EnumerateAsync(new RequestHistoryQuery { Route = route, FromUtc = noon, PageSize = 10 }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(afternoon.Id, String.Join(",", after.Objects.ConvertAll(item => item.Id)), "Same-day FromUtc returns only the later request");
                EnumerationResult<RequestHistoryEntry> before = await _Driver.RequestHistory.EnumerateAsync(new RequestHistoryQuery { Route = route, ToUtc = noon, PageSize = 10 }, token).ConfigureAwait(false);
                DatabaseAssert.Equal(morning.Id, String.Join(",", before.Objects.ConvertAll(item => item.Id)), "Same-day ToUtc returns only the earlier request");

                if (_Settings.Type == DatabaseTypeEnum.Postgresql)
                {
                    // A row stored as PostgreSQL timestamp text is rewritten by the upgrade to the ISO form
                    // the filters compare against.
                    await ExecuteRawAsync(new List<string> { "UPDATE request_history SET created_utc = '" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " 14:00:00+00' WHERE id = '" + afternoon.Id + "';" }, token).ConfigureAwait(false);
                    SchemaMigration normalize = Armada.Core.Database.Postgresql.Queries.TableQueries.GetMigrations().Find(item => item.Version == 107)!;
                    await ExecuteRawAsync(new List<string> { normalize.Statements[0] }, token).ConfigureAwait(false);
                    string rewritten = await ReadRawRequestCreatedAsync(afternoon.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal("String '" + day.AddHours(14).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "'", rewritten, "Upgrade rewrites timestamp text to ISO-8601");
                    EnumerationResult<RequestHistoryEntry> upgraded = await _Driver.RequestHistory.EnumerateAsync(new RequestHistoryQuery { Route = route, FromUtc = noon, PageSize = 10 }, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(afternoon.Id, String.Join(",", upgraded.Objects.ConvertAll(item => item.Id)), "Same-day FromUtc finds an upgraded row");
                }
            }
            finally
            {
                await _Driver.RequestHistory.DeleteAsync(morning.Id, null, token).ConfigureAwait(false);
                await _Driver.RequestHistory.DeleteAsync(afternoon.Id, null, token).ConfigureAwait(false);
            }
        }

        private async Task<string> ReadRawRequestCreatedAsync(string id, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT created_utc FROM request_history WHERE id = @id;";
                    DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "@id";
                    parameter.Value = id;
                    command.Parameters.Add(parameter);
                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return value == null ? "<none>" : value.GetType().Name + " '" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) + "'";
                }
            }
        }

        private async Task TestActiveWorkFootprintsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("footprint-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "footprint-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "footprint-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "footprint-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "footprint-captain", token).ConfigureAwait(false);
                TenantMetadata otherTenant = await fixture.CreateTenantAsync("footprint-other", token: token).ConfigureAwait(false);

                Voyage open = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "footprint-open", token, v => v.Status = VoyageStatusEnum.Open).ConfigureAwait(false);
                Voyage running = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "footprint-running", token, v => v.Status = VoyageStatusEnum.InProgress).ConfigureAwait(false);
                Voyage complete = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "footprint-complete", token, v => v.Status = VoyageStatusEnum.Complete).ConfigureAwait(false);
                Voyage cancelled = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "footprint-cancelled", token, v => v.Status = VoyageStatusEnum.Cancelled).ConfigureAwait(false);

                // Every mission of an active voyage counts whatever its own status.
                Mission finishedInOpen = await fixture.CreateMissionAsync(tenant.Id, user.Id, open.Id, vessel.Id, captain.Id, "footprint-finished-in-open", token, configure: m => m.Status = MissionStatusEnum.Complete).ConfigureAwait(false);
                Mission pendingInRunning = await fixture.CreateMissionAsync(tenant.Id, user.Id, running.Id, vessel.Id, captain.Id, "footprint-pending-in-running", token, configure: m => m.Status = MissionStatusEnum.Pending).ConfigureAwait(false);
                Mission activeInComplete = await fixture.CreateMissionAsync(tenant.Id, user.Id, complete.Id, vessel.Id, captain.Id, "footprint-active-in-complete", token, configure: m => m.Status = MissionStatusEnum.InProgress).ConfigureAwait(false);
                Mission activeInCancelled = await fixture.CreateMissionAsync(tenant.Id, user.Id, cancelled.Id, vessel.Id, captain.Id, "footprint-active-in-cancelled", token, configure: m => m.Status = MissionStatusEnum.Assigned).ConfigureAwait(false);

                // A mission without a voyage counts only while its own status is active.
                Mission standalonePending = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "footprint-standalone-pending", token, configure: m => m.Status = MissionStatusEnum.Pending).ConfigureAwait(false);
                Mission standaloneReview = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "footprint-standalone-review", token, configure: m => m.Status = MissionStatusEnum.Review).ConfigureAwait(false);
                Mission standaloneComplete = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "footprint-standalone-complete", token, configure: m => m.Status = MissionStatusEnum.Complete).ConfigureAwait(false);
                Mission standaloneFailed = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "footprint-standalone-failed", token, configure: m => m.Status = MissionStatusEnum.Failed).ConfigureAwait(false);
                Mission standaloneCancelled = await fixture.CreateMissionAsync(tenant.Id, user.Id, null!, vessel.Id, captain.Id, "footprint-standalone-cancelled", token, configure: m => m.Status = MissionStatusEnum.Cancelled).ConfigureAwait(false);

                List<string> expected = new List<string> { finishedInOpen.Id, pendingInRunning.Id, standalonePending.Id, standaloneReview.Id };
                expected.Sort(StringComparer.Ordinal);
                List<string> excluded = new List<string> { activeInComplete.Id, activeInCancelled.Id, standaloneComplete.Id, standaloneFailed.Id, standaloneCancelled.Id };

                List<ActiveWorkFootprint> scoped = await _Driver.Missions.EnumerateActiveWorkFootprintsAsync(tenant.Id, token).ConfigureAwait(false);
                List<string> scopedIds = scoped.ConvertAll(item => item.MissionId);
                scopedIds.Sort(StringComparer.Ordinal);
                DatabaseAssert.Equal(String.Join(",", expected), String.Join(",", scopedIds), "Tenant-scoped active work footprints");
                foreach (ActiveWorkFootprint footprint in scoped)
                    DatabaseAssert.Equal(vessel.Id, footprint.VesselId, "ActiveWorkFootprint.VesselId of " + footprint.MissionId);
                DatabaseAssert.Equal(open.Id, scoped.Find(item => item.MissionId == finishedInOpen.Id)!.VoyageId, "ActiveWorkFootprint.VoyageId of a voyage mission");
                DatabaseAssert.True(scoped.Find(item => item.MissionId == standalonePending.Id)!.VoyageId == null, "ActiveWorkFootprint.VoyageId of a standalone mission is null");

                List<ActiveWorkFootprint> otherScope = await _Driver.Missions.EnumerateActiveWorkFootprintsAsync(otherTenant.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(!otherScope.Exists(item => expected.Contains(item.MissionId)), "Another tenant's scope excludes these footprints");

                List<ActiveWorkFootprint> all = await _Driver.Missions.EnumerateActiveWorkFootprintsAsync(null, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(all, item => item.MissionId, expected.ToArray());
                DatabaseAssert.True(!all.Exists(item => excluded.Contains(item.MissionId)), "Unscoped footprints exclude terminal work");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestCheckRunCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                WorkflowProfile profile = await fixture.CreateWorkflowProfileAsync(graph.Tenant.Id, graph.User.Id, "checkrun-profile", graph.Fleet.Id, graph.Vessel.Id, token).ConfigureAwait(false);
                DateTime firstCreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                DateTime secondCreatedUtc = firstCreatedUtc.AddHours(1);
                CheckRun runA = await fixture.CreateCheckRunAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, graph.Mission.Id, graph.Voyage.Id, token, createdUtc: firstCreatedUtc).ConfigureAwait(false);
                CheckRun runB = await fixture.CreateCheckRunAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, graph.Mission.Id, graph.Voyage.Id, token, createdUtc: secondCreatedUtc).ConfigureAwait(false);

                CheckRun? read = await _Driver.CheckRuns.ReadAsync(runA.Id, null, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Check run read returned null");
                DatabaseAssert.Equal(graph.Tenant.Id, read.TenantId, "CheckRun.TenantId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "CheckRun.UserId");
                DatabaseAssert.Equal(graph.Vessel.Id, read.VesselId, "CheckRun.VesselId");
                DatabaseAssert.Equal(profile.Id, read.WorkflowProfileId, "CheckRun.WorkflowProfileId");
                DatabaseAssert.Equal(CheckRunStatusEnum.Passed, read.Status, "CheckRun.Status");
                DatabaseAssert.Equal(1, read.Artifacts.Count, "CheckRun.Artifacts.Count");

                read.Status = CheckRunStatusEnum.Failed;
                read.ExitCode = 1;
                read.Summary = "Fixture check failed.";
                CheckRun updated = await _Driver.CheckRuns.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(CheckRunStatusEnum.Failed, updated.Status, "Updated CheckRun.Status");
                DatabaseAssert.Equal(1, updated.ExitCode, "Updated CheckRun.ExitCode");
                DatabaseAssert.Equal("Fixture check failed.", updated.Summary, "Updated CheckRun.Summary");

                EnumerationResult<CheckRun> page = await _Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    PageNumber = 1,
                    PageSize = 10
                }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 2, "Check run enumeration should include created check runs");
                DatabaseAssert.ContainsIds(page.Objects, item => item.Id, runA.Id, runB.Id);

                CheckRunQuery dateQuery = new CheckRunQuery
                {
                    TenantId = graph.Tenant.Id,
                    VesselId = graph.Vessel.Id,
                    FromUtc = firstCreatedUtc,
                    ToUtc = secondCreatedUtc,
                    PageNumber = 1,
                    PageSize = 1
                };
                EnumerationResult<CheckRun> datedPage = await _Driver.CheckRuns.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2L, datedPage.TotalRecords, "CheckRun inclusive date range total before pagination");
                DatabaseAssert.Equal(1, datedPage.Objects.Count, "CheckRun date range page size");
                string firstPageId = datedPage.Objects[0].Id;

                dateQuery.PageNumber = 2;
                datedPage = await _Driver.CheckRuns.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2L, datedPage.TotalRecords, "CheckRun date range second-page total");
                DatabaseAssert.Equal(1, datedPage.Objects.Count, "CheckRun second-page size");
                DatabaseAssert.True(firstPageId != datedPage.Objects[0].Id, "CheckRun pages must contain different rows");
                DatabaseAssert.ContainsIds(new[] { firstPageId, datedPage.Objects[0].Id }, item => item, runA.Id, runB.Id);

                dateQuery.PageNumber = 1;
                dateQuery.PageSize = 10;
                dateQuery.FromUtc = secondCreatedUtc;
                dateQuery.ToUtc = null;
                datedPage = await _Driver.CheckRuns.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, datedPage.TotalRecords, "CheckRun lower bound excludes older row");
                DatabaseAssert.ContainsIds(datedPage.Objects, item => item.Id, runB.Id);

                dateQuery.FromUtc = null;
                dateQuery.ToUtc = firstCreatedUtc;
                datedPage = await _Driver.CheckRuns.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, datedPage.TotalRecords, "CheckRun upper bound excludes newer row");
                DatabaseAssert.ContainsIds(datedPage.Objects, item => item.Id, runA.Id);

                dateQuery.FromUtc = firstCreatedUtc.AddSeconds(1);
                dateQuery.ToUtc = secondCreatedUtc.AddSeconds(-1);
                datedPage = await _Driver.CheckRuns.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0L, datedPage.TotalRecords, "CheckRun date range excludes both rows");
                DatabaseAssert.Equal(0, datedPage.Objects.Count, "CheckRun empty date range has no rows");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestEnvironmentCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                DeploymentEnvironment environmentA = await fixture.CreateDeploymentEnvironmentAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    "environment-a",
                    EnvironmentKindEnum.Staging,
                    true,
                    token).ConfigureAwait(false);
                DeploymentEnvironment environmentB = await fixture.CreateDeploymentEnvironmentAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    "environment-b",
                    EnvironmentKindEnum.Production,
                    false,
                    token).ConfigureAwait(false);

                DeploymentEnvironment? read = await _Driver.Environments.ReadAsync(environmentA.Id, null, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Environment read returned null");
                DatabaseAssert.Equal(graph.Tenant.Id, read.TenantId, "Environment.TenantId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "Environment.UserId");
                DatabaseAssert.Equal(graph.Vessel.Id, read.VesselId, "Environment.VesselId");
                DatabaseAssert.Equal(EnvironmentKindEnum.Staging, read.Kind, "Environment.Kind");
                DatabaseAssert.Equal(true, read.IsDefault, "Environment.IsDefault");

                read.Kind = EnvironmentKindEnum.Production;
                read.RequiresApproval = true;
                read.BaseUrl = "https://production.example.test";
                DeploymentEnvironment updated = await _Driver.Environments.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(EnvironmentKindEnum.Production, updated.Kind, "Updated Environment.Kind");
                DatabaseAssert.Equal(true, updated.RequiresApproval, "Updated Environment.RequiresApproval");
                DatabaseAssert.Equal("https://production.example.test", updated.BaseUrl, "Updated Environment.BaseUrl");

                EnumerationResult<DeploymentEnvironment> page = await _Driver.Environments.EnumerateAsync(new DeploymentEnvironmentQuery
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    PageNumber = 1,
                    PageSize = 10
                }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 2, "Environment enumeration should include created environments");
                DatabaseAssert.ContainsIds(page.Objects, item => item.Id, environmentA.Id, environmentB.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestReleaseCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                WorkflowProfile profile = await fixture.CreateWorkflowProfileAsync(graph.Tenant.Id, graph.User.Id, "release-profile", graph.Fleet.Id, graph.Vessel.Id, token).ConfigureAwait(false);
                CheckRun checkRun = await fixture.CreateCheckRunAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, graph.Mission.Id, graph.Voyage.Id, token).ConfigureAwait(false);
                DateTime firstCreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                DateTime secondCreatedUtc = firstCreatedUtc.AddHours(1);
                Release releaseA = await fixture.CreateReleaseAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    profile.Id,
                    new[] { graph.Voyage.Id },
                    new[] { graph.Mission.Id },
                    new[] { checkRun.Id },
                    token, createdUtc: firstCreatedUtc).ConfigureAwait(false);
                Release releaseB = await fixture.CreateReleaseAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, null, null, null, token, createdUtc: secondCreatedUtc).ConfigureAwait(false);

                Release? read = await _Driver.Releases.ReadAsync(releaseA.Id, null, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Release read returned null");
                DatabaseAssert.Equal(graph.Tenant.Id, read.TenantId, "Release.TenantId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "Release.UserId");
                DatabaseAssert.Equal(graph.Vessel.Id, read.VesselId, "Release.VesselId");
                DatabaseAssert.Equal(profile.Id, read.WorkflowProfileId, "Release.WorkflowProfileId");
                DatabaseAssert.Equal(1, read.CheckRunIds.Count, "Release.CheckRunIds.Count");
                DatabaseAssert.Equal(1, read.Artifacts.Count, "Release.Artifacts.Count");

                read.Status = ReleaseStatusEnum.Shipped;
                read.PublishedUtc = DateTime.UtcNow;
                read.Summary = "Updated release summary";
                Release updated = await _Driver.Releases.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(ReleaseStatusEnum.Shipped, updated.Status, "Updated Release.Status");
                DatabaseAssert.Equal("Updated release summary", updated.Summary, "Updated Release.Summary");
                DatabaseAssert.True(updated.PublishedUtc.HasValue, "Updated Release.PublishedUtc");

                EnumerationResult<Release> page = await _Driver.Releases.EnumerateAsync(new ReleaseQuery
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    PageNumber = 1,
                    PageSize = 10
                }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 2, "Release enumeration should include created releases");
                DatabaseAssert.ContainsIds(page.Objects, item => item.Id, releaseA.Id, releaseB.Id);

                ReleaseQuery dateQuery = new ReleaseQuery
                {
                    TenantId = graph.Tenant.Id,
                    VesselId = graph.Vessel.Id,
                    FromUtc = firstCreatedUtc,
                    ToUtc = secondCreatedUtc,
                    PageNumber = 1,
                    PageSize = 1
                };
                EnumerationResult<Release> datedPage = await _Driver.Releases.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2L, datedPage.TotalRecords, "Release inclusive date range total before pagination");
                DatabaseAssert.Equal(1, datedPage.Objects.Count, "Release date range page size");
                string firstPageId = datedPage.Objects[0].Id;

                dateQuery.PageNumber = 2;
                datedPage = await _Driver.Releases.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2L, datedPage.TotalRecords, "Release date range second-page total");
                DatabaseAssert.Equal(1, datedPage.Objects.Count, "Release second-page size");
                DatabaseAssert.True(firstPageId != datedPage.Objects[0].Id, "Release pages must contain different rows");
                DatabaseAssert.ContainsIds(new[] { firstPageId, datedPage.Objects[0].Id }, item => item, releaseA.Id, releaseB.Id);

                dateQuery.PageNumber = 1;
                dateQuery.PageSize = 10;
                dateQuery.FromUtc = secondCreatedUtc;
                dateQuery.ToUtc = null;
                datedPage = await _Driver.Releases.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, datedPage.TotalRecords, "Release lower bound excludes older row");
                DatabaseAssert.ContainsIds(datedPage.Objects, item => item.Id, releaseB.Id);

                dateQuery.FromUtc = null;
                dateQuery.ToUtc = firstCreatedUtc;
                datedPage = await _Driver.Releases.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, datedPage.TotalRecords, "Release upper bound excludes newer row");
                DatabaseAssert.ContainsIds(datedPage.Objects, item => item.Id, releaseA.Id);

                dateQuery.FromUtc = firstCreatedUtc.AddSeconds(1);
                dateQuery.ToUtc = secondCreatedUtc.AddSeconds(-1);
                datedPage = await _Driver.Releases.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0L, datedPage.TotalRecords, "Release date range excludes both rows");
                DatabaseAssert.Equal(0, datedPage.Objects.Count, "Release empty date range has no rows");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestDeploymentCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                WorkflowProfile profile = await fixture.CreateWorkflowProfileAsync(graph.Tenant.Id, graph.User.Id, "deployment-profile", graph.Fleet.Id, graph.Vessel.Id, token).ConfigureAwait(false);
                Release release = await fixture.CreateReleaseAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, null, null, null, token).ConfigureAwait(false);
                DeploymentEnvironment environment = await fixture.CreateDeploymentEnvironmentAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    "deployment-environment",
                    EnvironmentKindEnum.Staging,
                    true,
                    token).ConfigureAwait(false);
                DateTime firstCreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                DateTime secondCreatedUtc = firstCreatedUtc.AddHours(1);
                Deployment deploymentA = await fixture.CreateDeploymentAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    environment.Id,
                    environment.Name,
                    profile.Id,
                    release.Id,
                    graph.Mission.Id,
                    graph.Voyage.Id,
                    token, createdUtc: firstCreatedUtc).ConfigureAwait(false);
                Deployment deploymentB = await fixture.CreateDeploymentAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    environment.Id,
                    environment.Name,
                    profile.Id,
                    null,
                    null,
                    null,
                    token, createdUtc: secondCreatedUtc).ConfigureAwait(false);

                Deployment? read = await _Driver.Deployments.ReadAsync(deploymentA.Id, null, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Deployment read returned null");
                DatabaseAssert.Equal(graph.Tenant.Id, read.TenantId, "Deployment.TenantId");
                DatabaseAssert.Equal(graph.User.Id, read.UserId, "Deployment.UserId");
                DatabaseAssert.Equal(graph.Vessel.Id, read.VesselId, "Deployment.VesselId");
                DatabaseAssert.Equal(profile.Id, read.WorkflowProfileId, "Deployment.WorkflowProfileId");
                DatabaseAssert.Equal(environment.Id, read.EnvironmentId, "Deployment.EnvironmentId");
                DatabaseAssert.Equal(release.Id, read.ReleaseId, "Deployment.ReleaseId");
                DatabaseAssert.Equal(DeploymentStatusEnum.Succeeded, read.Status, "Deployment.Status");
                DatabaseAssert.Equal(DeploymentVerificationStatusEnum.Passed, read.VerificationStatus, "Deployment.VerificationStatus");

                read.Status = DeploymentStatusEnum.RolledBack;
                read.VerificationStatus = DeploymentVerificationStatusEnum.Skipped;
                read.Summary = "Rolled back after verification.";
                Deployment updated = await _Driver.Deployments.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(DeploymentStatusEnum.RolledBack, updated.Status, "Updated Deployment.Status");
                DatabaseAssert.Equal(DeploymentVerificationStatusEnum.Skipped, updated.VerificationStatus, "Updated Deployment.VerificationStatus");
                DatabaseAssert.Equal("Rolled back after verification.", updated.Summary, "Updated Deployment.Summary");

                EnumerationResult<Deployment> page = await _Driver.Deployments.EnumerateAsync(new DeploymentQuery
                {
                    TenantId = graph.Tenant.Id,
                    UserId = graph.User.Id,
                    VesselId = graph.Vessel.Id,
                    PageNumber = 1,
                    PageSize = 10
                }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 2, "Deployment enumeration should include created deployments");
                DatabaseAssert.ContainsIds(page.Objects, item => item.Id, deploymentA.Id, deploymentB.Id);

                DeploymentQuery dateQuery = new DeploymentQuery
                {
                    TenantId = graph.Tenant.Id,
                    VesselId = graph.Vessel.Id,
                    FromUtc = firstCreatedUtc,
                    ToUtc = secondCreatedUtc,
                    PageNumber = 1,
                    PageSize = 1
                };
                EnumerationResult<Deployment> datedPage = await _Driver.Deployments.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2L, datedPage.TotalRecords, "Deployment inclusive date range total before pagination");
                DatabaseAssert.Equal(1, datedPage.Objects.Count, "Deployment date range page size");
                string firstPageId = datedPage.Objects[0].Id;

                dateQuery.PageNumber = 2;
                datedPage = await _Driver.Deployments.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(2L, datedPage.TotalRecords, "Deployment date range second-page total");
                DatabaseAssert.Equal(1, datedPage.Objects.Count, "Deployment second-page size");
                DatabaseAssert.True(firstPageId != datedPage.Objects[0].Id, "Deployment pages must contain different rows");
                DatabaseAssert.ContainsIds(new[] { firstPageId, datedPage.Objects[0].Id }, item => item, deploymentA.Id, deploymentB.Id);

                dateQuery.PageNumber = 1;
                dateQuery.PageSize = 10;
                dateQuery.FromUtc = secondCreatedUtc;
                dateQuery.ToUtc = null;
                datedPage = await _Driver.Deployments.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, datedPage.TotalRecords, "Deployment lower bound excludes older row");
                DatabaseAssert.ContainsIds(datedPage.Objects, item => item.Id, deploymentB.Id);

                dateQuery.FromUtc = null;
                dateQuery.ToUtc = firstCreatedUtc;
                datedPage = await _Driver.Deployments.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1L, datedPage.TotalRecords, "Deployment upper bound excludes newer row");
                DatabaseAssert.ContainsIds(datedPage.Objects, item => item.Id, deploymentA.Id);

                dateQuery.FromUtc = firstCreatedUtc.AddSeconds(1);
                dateQuery.ToUtc = secondCreatedUtc.AddSeconds(-1);
                datedPage = await _Driver.Deployments.EnumerateAsync(dateQuery, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0L, datedPage.TotalRecords, "Deployment date range excludes both rows");
                DatabaseAssert.Equal(0, datedPage.Objects.Count, "Deployment empty date range has no rows");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestObjectiveUnreadableStoredDataAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("unreadable-objective", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "unreadable-objective", token: token).ConfigureAwait(false);
                Objective readable = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "unreadable-readable", token: token).ConfigureAwait(false);
                Objective badJson = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "unreadable-json", token: token).ConfigureAwait(false);
                Objective badEnum = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "unreadable-enum", token: token).ConfigureAwait(false);

                await ExecuteRawAsync(new List<string>
                {
                    "UPDATE objectives SET blocked_by_objective_ids_json = '[\"obj_truncated' WHERE id = '" + badJson.Id + "';",
                    "UPDATE objectives SET status = 'NoSuchStatus' WHERE id = '" + badEnum.Id + "';"
                }, token).ConfigureAwait(false);

                string? jsonError = null;
                try { await _Driver.Objectives.ReadAsync(badJson.Id, token).ConfigureAwait(false); }
                catch (StoredObjectiveDataException ex) { jsonError = ex.Message; }
                DatabaseAssert.True(jsonError != null && jsonError.Contains(badJson.Id) && jsonError.Contains("blocked_by_objective_ids_json"),
                    "A malformed blocker list is a read error naming the row and field, never an empty list: " + jsonError);

                string? enumError = null;
                try { await _Driver.Objectives.ReadAsync(badEnum.Id, token).ConfigureAwait(false); }
                catch (StoredObjectiveDataException ex) { enumError = ex.Message; }
                DatabaseAssert.True(enumError != null && enumError.Contains(badEnum.Id) && enumError.Contains("status"),
                    "An unknown status is a read error naming the row and field, never Draft: " + enumError);

                List<Objective> listed = await _Driver.Objectives.EnumerateAsync(tenant.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, listed.Count, "Only the readable objective is listed");
                DatabaseAssert.Equal(readable.Id, listed[0].Id, "The readable objective is listed");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestObjectiveCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("objective-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "objective-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "objective-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "objective-vessel", token).ConfigureAwait(false);

                Objective parent = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-parent", null, new[] { vessel.Id }, token).ConfigureAwait(false);
                Objective objectiveA = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-a", parent.Id, new[] { vessel.Id }, token).ConfigureAwait(false);
                Objective objectiveB = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-b", null, null, token).ConfigureAwait(false);

                objectiveA.BlockedByObjectiveIds.Add(parent.Id);
                objectiveA.RefinementSessionIds.Add("ors_seed");
                objectiveA = await _Driver.Objectives.UpdateAsync(objectiveA, token).ConfigureAwait(false);

                Objective? read = await _Driver.Objectives.ReadAsync(tenant.Id, user.Id, objectiveA.Id, token).ConfigureAwait(false);
                read = DatabaseAssert.NotNull(read, "Objective read returned null");
                DatabaseAssert.HasPrefix(read.Id, "obj_", "Objective.Id");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "Objective.TenantId");
                DatabaseAssert.Equal(user.Id, read.UserId, "Objective.UserId");
                DatabaseAssert.Equal(parent.Id, read.ParentObjectiveId, "Objective.ParentObjectiveId");
                DatabaseAssert.Equal(1, read.BlockedByObjectiveIds.Count, "Objective.BlockedByObjectiveIds.Count");
                DatabaseAssert.Equal(1, read.RefinementSessionIds.Count, "Objective.RefinementSessionIds.Count");
                DatabaseAssert.Equal(vessel.Id, read.VesselIds[0], "Objective.VesselIds[0]");
                DatabaseAssert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", read.Preparation.Source?.ResolvedCommit, "Objective.Preparation.Source.ResolvedCommit");
                DatabaseAssert.Equal(1, read.Preparation.Claims.Count, "Objective.Preparation.Claims.Count");
                DatabaseAssert.Equal(ObjectivePreparationClaimStateEnum.Verified, read.Preparation.Claims[0].State, "Objective.Preparation.Claims[0].State");

                read.Priority = ObjectivePriorityEnum.P0;
                read.Rank = 3;
                read.BacklogState = ObjectiveBacklogStateEnum.ReadyForPlanning;
                read.RefinementSummary = "Updated objective summary";
                read.AcceptanceCriteria.Add("Persist refinement transcript");
                read.Preparation.Claims[0].State = ObjectivePreparationClaimStateEnum.NeedsRecheck;
                read.Preparation.Claims[0].InvalidationReason = "Fixture anchor changed.";
                Objective updated = await _Driver.Objectives.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(ObjectivePriorityEnum.P0, updated.Priority, "Updated Objective.Priority");
                DatabaseAssert.Equal(3, updated.Rank, "Updated Objective.Rank");
                DatabaseAssert.Equal(ObjectiveBacklogStateEnum.ReadyForPlanning, updated.BacklogState, "Updated Objective.BacklogState");
                DatabaseAssert.Equal("Updated objective summary", updated.RefinementSummary, "Updated Objective.RefinementSummary");
                DatabaseAssert.Equal(4, updated.AcceptanceCriteria.Count, "Updated Objective.AcceptanceCriteria.Count");
                DatabaseAssert.Equal(ObjectivePreparationClaimStateEnum.NeedsRecheck, updated.Preparation.Claims[0].State, "Updated Objective.Preparation.Claims[0].State");
                DatabaseAssert.Equal("Fixture anchor changed.", updated.Preparation.Claims[0].InvalidationReason, "Updated Objective.Preparation.Claims[0].InvalidationReason");

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Objective persisted = DatabaseAssert.NotNull(await reopened.Objectives.ReadAsync(tenant.Id, user.Id, read.Id, token).ConfigureAwait(false), "Objective reopened");
                    DatabaseAssert.True(!persisted.AutoDispatchEnabled, "Objective dispatch remains disabled");
                    DatabaseAssert.Equal(ObjectivePreparationClaimStateEnum.NeedsRecheck, persisted.Preparation.Claims[0].State, "Objective preparation invalidation persists");
                    DatabaseAssert.Equal("Fixture anchor changed.", persisted.Preparation.Claims[0].InvalidationReason, "Objective invalidation evidence persists");
                    DatabaseAssert.Equal(parent.Id, persisted.BlockedByObjectiveIds[0], "Objective blocker persists");
                    DatabaseAssert.UtcInstant(updated.CreatedUtc, persisted.CreatedUtc, "Reopened Objective.CreatedUtc");
                    DatabaseAssert.UtcInstant(updated.LastUpdateUtc, persisted.LastUpdateUtc, "Reopened Objective.LastUpdateUtc");
                }
                DatabaseAssert.UtcInstant(objectiveA.CreatedUtc, read.CreatedUtc, "Objective.CreatedUtc");

                List<Objective> tenantObjectives = await _Driver.Objectives.EnumerateAsync(tenant.Id, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(tenantObjectives, item => item.Id, parent.Id, objectiveA.Id, objectiveB.Id);

                List<Objective> scopedObjectives = await _Driver.Objectives.EnumerateAsync(tenant.Id, user.Id, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(scopedObjectives, item => item.Id, parent.Id, objectiveA.Id, objectiveB.Id);

                DatabaseAssert.True(await _Driver.Objectives.ExistsAnyAsync(token).ConfigureAwait(false), "Objective ExistsAnyAsync should return true");
                DatabaseAssert.True(await _Driver.Objectives.ExistsAsync(objectiveA.Id, token).ConfigureAwait(false), "Objective ExistsAsync should return true");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestObjectiveRefinementUnreadableStatusAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("unreadable-refinement", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "unreadable-refinement", token: token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "unreadable-refinement-captain", token).ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "unreadable-refinement-objective", null, null, token).ConfigureAwait(false);
                ObjectiveRefinementSession readable = await fixture.CreateObjectiveRefinementSessionAsync(
                    tenant.Id, user.Id, objective.Id, captain.Id, null, ObjectiveRefinementSessionStatusEnum.Active, token).ConfigureAwait(false);
                ObjectiveRefinementSession unreadable = await fixture.CreateObjectiveRefinementSessionAsync(
                    tenant.Id, user.Id, objective.Id, captain.Id, null, ObjectiveRefinementSessionStatusEnum.Active, token).ConfigureAwait(false);

                await ExecuteRawAsync(new List<string>
                {
                    "UPDATE objective_refinement_sessions SET status = 'NoSuchStatus' WHERE id = '" + unreadable.Id + "';"
                }, token).ConfigureAwait(false);

                string? readError = null;
                try { await _Driver.ObjectiveRefinementSessions.ReadAsync(unreadable.Id, token).ConfigureAwait(false); }
                catch (StoredRefinementSessionDataException ex) { readError = ex.Message; }
                DatabaseAssert.True(readError != null && readError.Contains(unreadable.Id) && readError.Contains("status"),
                    "An unknown status is a read error naming the row and field, never Created: " + readError);

                List<ObjectiveRefinementSession> listed = await _Driver.ObjectiveRefinementSessions.EnumerateByObjectiveAsync(objective.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, listed.Count, "Only the readable refinement session is listed");
                DatabaseAssert.Equal(readable.Id, listed[0].Id, "The readable refinement session is listed");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestObjectiveRefinementCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("refinement-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "refinement-user", token: token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "refinement-captain", token).ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "refinement-objective", null, null, token).ConfigureAwait(false);

                ObjectiveRefinementSession sessionA = await fixture.CreateObjectiveRefinementSessionAsync(
                    tenant.Id, user.Id, objective.Id, captain.Id, null, ObjectiveRefinementSessionStatusEnum.Active, token).ConfigureAwait(false);
                ObjectiveRefinementSession sessionB = await fixture.CreateObjectiveRefinementSessionAsync(
                    tenant.Id, user.Id, objective.Id, captain.Id, null, ObjectiveRefinementSessionStatusEnum.Created, token).ConfigureAwait(false);

                ObjectiveRefinementMessage messageA = await fixture.CreateObjectiveRefinementMessageAsync(
                    sessionA.Id, objective.Id, tenant.Id, user.Id, "User", 1, "Clarify rollback criteria", false, token).ConfigureAwait(false);
                ObjectiveRefinementMessage messageB = await fixture.CreateObjectiveRefinementMessageAsync(
                    sessionA.Id, objective.Id, tenant.Id, user.Id, "Assistant", 2, "Use staged rollout and verify alerts.", false, token).ConfigureAwait(false);

                ObjectiveRefinementSession? readSession = await _Driver.ObjectiveRefinementSessions.ReadAsync(tenant.Id, user.Id, sessionA.Id, token).ConfigureAwait(false);
                readSession = DatabaseAssert.NotNull(readSession, "Objective refinement session read returned null");
                DatabaseAssert.HasPrefix(readSession.Id, "ors_", "ObjectiveRefinementSession.Id");
                DatabaseAssert.Equal(objective.Id, readSession.ObjectiveId, "ObjectiveRefinementSession.ObjectiveId");
                DatabaseAssert.Equal(captain.Id, readSession.CaptainId, "ObjectiveRefinementSession.CaptainId");

                readSession.Status = ObjectiveRefinementSessionStatusEnum.Completed;
                readSession.CompletedUtc = DateTime.UtcNow;
                ObjectiveRefinementSession updatedSession = await _Driver.ObjectiveRefinementSessions.UpdateAsync(readSession, token).ConfigureAwait(false);
                DatabaseAssert.Equal(ObjectiveRefinementSessionStatusEnum.Completed, updatedSession.Status, "Updated ObjectiveRefinementSession.Status");
                DatabaseAssert.True(updatedSession.CompletedUtc.HasValue, "Updated ObjectiveRefinementSession.CompletedUtc should have a value");

                messageB.IsSelected = true;
                messageB.Content = "Use staged rollout, verify alerts, and preserve request replay.";
                ObjectiveRefinementMessage updatedMessage = await _Driver.ObjectiveRefinementMessages.UpdateAsync(messageB, token).ConfigureAwait(false);
                DatabaseAssert.Equal(true, updatedMessage.IsSelected, "Updated ObjectiveRefinementMessage.IsSelected");
                DatabaseAssert.Equal("Use staged rollout, verify alerts, and preserve request replay.", updatedMessage.Content, "Updated ObjectiveRefinementMessage.Content");

                List<ObjectiveRefinementSession> byObjective = await _Driver.ObjectiveRefinementSessions.EnumerateByObjectiveAsync(objective.Id, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(byObjective, item => item.Id, sessionA.Id, sessionB.Id);

                List<ObjectiveRefinementSession> byCaptain = await _Driver.ObjectiveRefinementSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(byCaptain, item => item.Id, sessionA.Id, sessionB.Id);

                List<ObjectiveRefinementSession> byStatus = await _Driver.ObjectiveRefinementSessions.EnumerateByStatusAsync(ObjectiveRefinementSessionStatusEnum.Completed, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(byStatus, item => item.Id, sessionA.Id);

                List<ObjectiveRefinementMessage> messagesBySession = await _Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(sessionA.Id, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(messagesBySession, item => item.Id, messageA.Id, messageB.Id);

                List<ObjectiveRefinementMessage> messagesByObjective = await _Driver.ObjectiveRefinementMessages.EnumerateByObjectiveAsync(objective.Id, token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(messagesByObjective, item => item.Id, messageA.Id, messageB.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestPlanningSessionCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<string> sessionIds = new List<string>();
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("planning-tenant", token: token).ConfigureAwait(false);
                UserMaster owner = await fixture.CreateUserAsync(tenant.Id, "planning-owner", token: token).ConfigureAwait(false);
                UserMaster colleague = await fixture.CreateUserAsync(tenant.Id, "planning-colleague", token: token).ConfigureAwait(false);
                TenantMetadata otherTenant = await fixture.CreateTenantAsync("planning-other", token: token).ConfigureAwait(false);
                UserMaster outsider = await fixture.CreateUserAsync(otherTenant.Id, "planning-outsider", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, owner.Id, "planning-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, owner.Id, fleet.Id, "planning-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, owner.Id, "planning-captain", token).ConfigureAwait(false);
                Captain otherCaptain = await fixture.CreateCaptainAsync(otherTenant.Id, outsider.Id, "planning-other-captain", token).ConfigureAwait(false);

                DateTime startedUtc = DateTime.UtcNow.AddMinutes(-3);
                PlanningSession owned = new PlanningSession
                {
                    TenantId = tenant.Id,
                    UserId = owner.Id,
                    CaptainId = captain.Id,
                    VesselId = vessel.Id,
                    FleetId = fleet.Id,
                    DockId = "dck_planning",
                    BranchName = "armada/planning-round-trip",
                    Title = "Plan the release",
                    Status = PlanningSessionStatusEnum.Active,
                    PipelineId = "ppl_planning",
                    ObjectiveId = "obj_planning",
                    SelectedPlaybooks = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_first", DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference },
                        new SelectedPlaybook { PlaybookId = "pbk_second", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                    },
                    ProcessId = 4242,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-4),
                    StartedUtc = startedUtc
                };
                await _Driver.PlanningSessions.CreateAsync(owned, token).ConfigureAwait(false);
                sessionIds.Add(owned.Id);
                PlanningSession shared = new PlanningSession
                {
                    TenantId = tenant.Id,
                    UserId = colleague.Id,
                    CaptainId = captain.Id,
                    VesselId = vessel.Id,
                    Title = "Plan the rollback",
                    Status = PlanningSessionStatusEnum.Created
                };
                await _Driver.PlanningSessions.CreateAsync(shared, token).ConfigureAwait(false);
                sessionIds.Add(shared.Id);
                PlanningSession foreign = new PlanningSession
                {
                    TenantId = otherTenant.Id,
                    UserId = outsider.Id,
                    CaptainId = otherCaptain.Id,
                    VesselId = "vsl_foreign",
                    Title = "Another tenant's plan",
                    Status = PlanningSessionStatusEnum.Active
                };
                await _Driver.PlanningSessions.CreateAsync(foreign, token).ConfigureAwait(false);
                sessionIds.Add(foreign.Id);

                PlanningSession read = DatabaseAssert.NotNull(await _Driver.PlanningSessions.ReadAsync(owned.Id, token).ConfigureAwait(false), "Planning session read returned null");
                DatabaseAssert.HasPrefix(read.Id, "psn_", "PlanningSession.Id");
                DatabaseAssert.Equal(tenant.Id, read.TenantId, "PlanningSession.TenantId");
                DatabaseAssert.Equal(owner.Id, read.UserId, "PlanningSession.UserId");
                DatabaseAssert.Equal(captain.Id, read.CaptainId, "PlanningSession.CaptainId");
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "PlanningSession.VesselId");
                DatabaseAssert.Equal(fleet.Id, read.FleetId, "PlanningSession.FleetId");
                DatabaseAssert.Equal("dck_planning", read.DockId, "PlanningSession.DockId");
                DatabaseAssert.Equal("armada/planning-round-trip", read.BranchName, "PlanningSession.BranchName");
                DatabaseAssert.Equal("Plan the release", read.Title, "PlanningSession.Title");
                DatabaseAssert.Equal(PlanningSessionStatusEnum.Active, read.Status, "PlanningSession.Status");
                DatabaseAssert.Equal("ppl_planning", read.PipelineId, "PlanningSession.PipelineId");
                DatabaseAssert.Equal("obj_planning", read.ObjectiveId, "PlanningSession.ObjectiveId");
                DatabaseAssert.Equal(2, read.SelectedPlaybooks.Count, "PlanningSession.SelectedPlaybooks.Count");
                DatabaseAssert.Equal("pbk_first", read.SelectedPlaybooks[0].PlaybookId, "PlanningSession.SelectedPlaybooks[0].PlaybookId");
                DatabaseAssert.Equal(PlaybookDeliveryModeEnum.InstructionWithReference, read.SelectedPlaybooks[0].DeliveryMode, "PlanningSession.SelectedPlaybooks[0].DeliveryMode");
                DatabaseAssert.Equal((int?)4242, read.ProcessId, "PlanningSession.ProcessId");
                DatabaseAssert.Equal<string?>(null, read.FailureReason, "PlanningSession.FailureReason");
                AssertSameUtcInstant(owned.CreatedUtc, read.CreatedUtc, "PlanningSession.CreatedUtc");
                AssertSameUtcInstant(startedUtc, read.StartedUtc, "PlanningSession.StartedUtc");
                DatabaseAssert.True(read.CompletedUtc == null, "PlanningSession.CompletedUtc should be null");
                AssertSameUtcInstant(owned.LastUpdateUtc, read.LastUpdateUtc, "PlanningSession.LastUpdateUtc");

                DatabaseAssert.True(await _Driver.PlanningSessions.ReadAsync("pls_absent", token).ConfigureAwait(false) == null, "An unknown planning session reads as null");
                DatabaseAssert.True(await _Driver.PlanningSessions.ReadAsync(otherTenant.Id, owned.Id, token).ConfigureAwait(false) == null, "Another tenant cannot read the session");
                DatabaseAssert.True(await _Driver.PlanningSessions.ReadAsync(tenant.Id, owned.Id, token).ConfigureAwait(false) != null, "The owning tenant reads the session");
                DatabaseAssert.True(await _Driver.PlanningSessions.ReadAsync(tenant.Id, colleague.Id, owned.Id, token).ConfigureAwait(false) == null, "Another user in the tenant cannot read the session through the user scope");
                DatabaseAssert.True(await _Driver.PlanningSessions.ReadAsync(tenant.Id, owner.Id, owned.Id, token).ConfigureAwait(false) != null, "The owning user reads the session");

                read.Status = PlanningSessionStatusEnum.Stopped;
                read.CompletedUtc = DateTime.UtcNow;
                read.FailureReason = "Stopped by operator";
                read.ProcessId = null;
                read.BranchName = null;
                read.SelectedPlaybooks = new List<SelectedPlaybook>();
                await _Driver.PlanningSessions.UpdateAsync(read, token).ConfigureAwait(false);
                PlanningSession updated = DatabaseAssert.NotNull(await _Driver.PlanningSessions.ReadAsync(owned.Id, token).ConfigureAwait(false), "Updated planning session read returned null");
                DatabaseAssert.Equal(PlanningSessionStatusEnum.Stopped, updated.Status, "Updated PlanningSession.Status");
                DatabaseAssert.Equal("Stopped by operator", updated.FailureReason, "Updated PlanningSession.FailureReason");
                DatabaseAssert.Equal((int?)null, updated.ProcessId, "Updated PlanningSession.ProcessId");
                DatabaseAssert.Equal<string?>(null, updated.BranchName, "Updated PlanningSession.BranchName");
                DatabaseAssert.Equal(0, updated.SelectedPlaybooks.Count, "Updated PlanningSession.SelectedPlaybooks.Count");
                AssertSameUtcInstant(read.CompletedUtc, updated.CompletedUtc, "Updated PlanningSession.CompletedUtc");
                AssertSameUtcInstant(owned.CreatedUtc, updated.CreatedUtc, "Updated PlanningSession.CreatedUtc is unchanged");

                // Every list is ordered by last update, newest first; the update moved the owned session to the top.
                List<PlanningSession> tenantSessions = await _Driver.PlanningSessions.EnumerateAsync(tenant.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(owned.Id + "," + shared.Id, String.Join(",", tenantSessions.ConvertAll(item => item.Id)), "Tenant planning sessions, newest update first");
                List<PlanningSession> userSessions = await _Driver.PlanningSessions.EnumerateAsync(tenant.Id, colleague.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(shared.Id, String.Join(",", userSessions.ConvertAll(item => item.Id)), "User-scoped planning sessions");
                List<PlanningSession> allSessions = (await _Driver.PlanningSessions.EnumerateAsync(token).ConfigureAwait(false)).FindAll(item => sessionIds.Contains(item.Id));
                DatabaseAssert.Equal(owned.Id + "," + foreign.Id + "," + shared.Id, String.Join(",", allSessions.ConvertAll(item => item.Id)), "All planning sessions, newest update first");
                List<PlanningSession> captainSessions = await _Driver.PlanningSessions.EnumerateByCaptainAsync(captain.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(owned.Id + "," + shared.Id, String.Join(",", captainSessions.ConvertAll(item => item.Id)), "Planning sessions by captain");
                List<PlanningSession> activeSessions = (await _Driver.PlanningSessions.EnumerateByStatusAsync(PlanningSessionStatusEnum.Active, token).ConfigureAwait(false)).FindAll(item => sessionIds.Contains(item.Id));
                DatabaseAssert.Equal(foreign.Id, String.Join(",", activeSessions.ConvertAll(item => item.Id)), "Active planning sessions");

                PlanningSessionMessage second = new PlanningSessionMessage { PlanningSessionId = owned.Id, TenantId = tenant.Id, UserId = owner.Id, Role = "Assistant", Sequence = 2, Content = "Ship behind a flag." };
                PlanningSessionMessage first = new PlanningSessionMessage { PlanningSessionId = owned.Id, TenantId = tenant.Id, UserId = owner.Id, Role = "User", Sequence = 1, Content = "How should we ship this?" };
                PlanningSessionMessage third = new PlanningSessionMessage { PlanningSessionId = owned.Id, TenantId = tenant.Id, UserId = owner.Id, Role = "User", Sequence = 3, Content = String.Empty };
                await _Driver.PlanningSessionMessages.CreateAsync(second, token).ConfigureAwait(false);
                await _Driver.PlanningSessionMessages.CreateAsync(first, token).ConfigureAwait(false);
                await _Driver.PlanningSessionMessages.CreateAsync(third, token).ConfigureAwait(false);
                PlanningSessionMessage sharedMessage = new PlanningSessionMessage { PlanningSessionId = shared.Id, TenantId = tenant.Id, UserId = colleague.Id, Role = "User", Sequence = 1, Content = "Rollback plan?" };
                await _Driver.PlanningSessionMessages.CreateAsync(sharedMessage, token).ConfigureAwait(false);

                List<PlanningSessionMessage> transcript = await _Driver.PlanningSessionMessages.EnumerateBySessionAsync(owned.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(first.Id + "," + second.Id + "," + third.Id, String.Join(",", transcript.ConvertAll(item => item.Id)), "Transcript in sequence order");

                bool duplicateRefused = false;
                try
                {
                    await _Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage { PlanningSessionId = owned.Id, TenantId = tenant.Id, UserId = owner.Id, Role = "User", Sequence = 2, Content = "Duplicate" }, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not NotSupportedException)
                {
                    duplicateRefused = true;
                }
                DatabaseAssert.True(duplicateRefused, "A second message with the same sequence in one session is refused");

                PlanningSessionMessage readMessage = DatabaseAssert.NotNull(await _Driver.PlanningSessionMessages.ReadAsync(second.Id, token).ConfigureAwait(false), "Planning message read returned null");
                DatabaseAssert.HasPrefix(readMessage.Id, "psm_", "PlanningSessionMessage.Id");
                DatabaseAssert.Equal(owned.Id, readMessage.PlanningSessionId, "PlanningSessionMessage.PlanningSessionId");
                DatabaseAssert.Equal(tenant.Id, readMessage.TenantId, "PlanningSessionMessage.TenantId");
                DatabaseAssert.Equal(owner.Id, readMessage.UserId, "PlanningSessionMessage.UserId");
                DatabaseAssert.Equal("Assistant", readMessage.Role, "PlanningSessionMessage.Role");
                DatabaseAssert.Equal(2, readMessage.Sequence, "PlanningSessionMessage.Sequence");
                DatabaseAssert.Equal("Ship behind a flag.", readMessage.Content, "PlanningSessionMessage.Content");
                DatabaseAssert.Equal(false, readMessage.IsSelectedForDispatch, "PlanningSessionMessage.IsSelectedForDispatch");
                AssertSameUtcInstant(second.CreatedUtc, readMessage.CreatedUtc, "PlanningSessionMessage.CreatedUtc");
                PlanningSessionMessage readEmpty = DatabaseAssert.NotNull(await _Driver.PlanningSessionMessages.ReadAsync(third.Id, token).ConfigureAwait(false), "Empty planning message read returned null");
                DatabaseAssert.Equal(String.Empty, readEmpty.Content, "An empty message reads as empty content");

                readMessage.IsSelectedForDispatch = true;
                readMessage.Content = "Ship behind a flag and stage the rollout.";
                await _Driver.PlanningSessionMessages.UpdateAsync(readMessage, token).ConfigureAwait(false);
                PlanningSessionMessage updatedMessage = DatabaseAssert.NotNull(await _Driver.PlanningSessionMessages.ReadAsync(second.Id, token).ConfigureAwait(false), "Updated planning message read returned null");
                DatabaseAssert.Equal(true, updatedMessage.IsSelectedForDispatch, "Updated PlanningSessionMessage.IsSelectedForDispatch");
                DatabaseAssert.Equal("Ship behind a flag and stage the rollout.", updatedMessage.Content, "Updated PlanningSessionMessage.Content");

                await _Driver.PlanningSessionMessages.DeleteAsync(third.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.PlanningSessionMessages.ReadAsync(third.Id, token).ConfigureAwait(false) == null, "A deleted message reads as null");

                await _Driver.PlanningSessionMessages.DeleteBySessionAsync(shared.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(0, (await _Driver.PlanningSessionMessages.EnumerateBySessionAsync(shared.Id, token).ConfigureAwait(false)).Count, "DeleteBySession empties that session's transcript");
                DatabaseAssert.Equal(2, (await _Driver.PlanningSessionMessages.EnumerateBySessionAsync(owned.Id, token).ConfigureAwait(false)).Count, "DeleteBySession leaves other transcripts in place");

                await _Driver.PlanningSessions.DeleteAsync(owned.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.PlanningSessions.ReadAsync(owned.Id, token).ConfigureAwait(false) == null, "A deleted planning session reads as null");
                DatabaseAssert.True(await _Driver.PlanningSessionMessages.ReadAsync(first.Id, token).ConfigureAwait(false) == null, "Deleting a session cascades to its messages");
                DatabaseAssert.Equal(0, (await _Driver.PlanningSessionMessages.EnumerateBySessionAsync(owned.Id, token).ConfigureAwait(false)).Count, "No transcript remains for a deleted session");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    foreach (string id in sessionIds)
                        await _Driver.PlanningSessions.DeleteAsync(id, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private static void AssertSameUtcInstant(DateTime? expected, DateTime? actual, string fieldName)
        {
            DatabaseAssert.True(expected.HasValue && actual.HasValue, fieldName + " should have a value");
            DatabaseAssert.Equal(DateTimeKind.Utc, actual!.Value.Kind, fieldName + ".Kind");
            DatabaseAssert.True(Math.Abs((expected!.Value.ToUniversalTime() - actual.Value).TotalMilliseconds) < 1, fieldName + " should round-trip (expected " + expected.Value.ToString("O") + ", found " + actual.Value.ToString("O") + ")");
        }

        private async Task TestObjectiveForeignKeysAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("objective-fk-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "objective-fk-user", token: token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "objective-fk-captain", token).ConfigureAwait(false);

                Objective parent = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-parent-fk", null, null, token).ConfigureAwait(false);
                Objective child = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-child-fk", parent.Id, null, token).ConfigureAwait(false);
                await _Driver.Objectives.DeleteAsync(parent.Id, token).ConfigureAwait(false);

                Objective? reloadedChild = await _Driver.Objectives.ReadAsync(child.Id, token).ConfigureAwait(false);
                reloadedChild = DatabaseAssert.NotNull(reloadedChild, "Child objective should remain after parent delete");
                DatabaseAssert.True(String.IsNullOrWhiteSpace(reloadedChild.ParentObjectiveId), "Child objective parent link should be nulled by FK");

                Objective objective = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "objective-cascade", null, null, token).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateObjectiveRefinementSessionAsync(
                    tenant.Id, user.Id, objective.Id, captain.Id, null, ObjectiveRefinementSessionStatusEnum.Active, token).ConfigureAwait(false);
                ObjectiveRefinementMessage message = await fixture.CreateObjectiveRefinementMessageAsync(
                    session.Id, objective.Id, tenant.Id, user.Id, "Assistant", 1, "Cascade this transcript", true, token).ConfigureAwait(false);

                await _Driver.Objectives.DeleteAsync(objective.Id, token).ConfigureAwait(false);

                ObjectiveRefinementSession? deletedSession = await _Driver.ObjectiveRefinementSessions.ReadAsync(session.Id, token).ConfigureAwait(false);
                ObjectiveRefinementMessage? deletedMessage = await _Driver.ObjectiveRefinementMessages.ReadAsync(message.Id, token).ConfigureAwait(false);
                List<ObjectiveRefinementMessage> remainingMessages = await _Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id, token).ConfigureAwait(false);

                DatabaseAssert.True(deletedSession == null, "Refinement session should be deleted by objective cascade");
                DatabaseAssert.True(deletedMessage == null, "Refinement message should be deleted by objective cascade");
                DatabaseAssert.Equal(0, remainingMessages.Count, "Refinement messages by session should be empty after cascade");
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task TestTenantAuthCascadeDeleteAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            TenantMetadata tenant = await fixture.CreateTenantAsync("cascade-tenant", token: token).ConfigureAwait(false);
            UserMaster user = await fixture.CreateUserAsync(tenant.Id, "cascade-user", token: token).ConfigureAwait(false);
            Credential credential = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "cascade-credential", token: token).ConfigureAwait(false);
            await _Driver.Tenants.DeleteAsync(tenant.Id, token).ConfigureAwait(false);

            DatabaseAssert.True(await _Driver.Tenants.ReadAsync(tenant.Id, token).ConfigureAwait(false) == null, "Tenant should be deleted");
            DatabaseAssert.True(await _Driver.Users.ReadByIdAsync(user.Id, token).ConfigureAwait(false) == null, "User should be deleted by tenant cascade");
            DatabaseAssert.True(await _Driver.Credentials.ReadByIdAsync(credential.Id, token).ConfigureAwait(false) == null, "Credential should be deleted by tenant cascade");
        }

        private async Task TestTenantDeleteFencedByOperationalDataAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            TenantMetadata tenant = await fixture.CreateTenantAsync("cascade-tenant", token: token).ConfigureAwait(false);
            UserMaster user = await fixture.CreateUserAsync(tenant.Id, "cascade-user", token: token).ConfigureAwait(false);
            Credential credential = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "cascade-credential", token: token).ConfigureAwait(false);
            Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "cascade-fleet", token).ConfigureAwait(false);
            Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "cascade-vessel", token).ConfigureAwait(false);
            Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "cascade-captain", token).ConfigureAwait(false);
            Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "cascade-voyage", token).ConfigureAwait(false);
            Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "cascade-mission", token).ConfigureAwait(false);
            Dock dock = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, captain.Id, token).ConfigureAwait(false);
            Signal signal = await fixture.CreateSignalAsync(tenant.Id, user.Id, captain.Id, token).ConfigureAwait(false);
            ArmadaEvent evt = await fixture.CreateEventAsync(tenant.Id, user.Id, mission.Id, voyage.Id, vessel.Id, captain.Id, token).ConfigureAwait(false);
            MergeEntry merge = await fixture.CreateMergeEntryAsync(tenant.Id, user.Id, mission.Id, vessel.Id, token).ConfigureAwait(false);

            bool deleteFailed = false;
            try
            {
                await _Driver.Tenants.DeleteAsync(tenant.Id, token).ConfigureAwait(false);
            }
            catch
            {
                deleteFailed = true;
            }

            DatabaseAssert.True(deleteFailed, "Tenant delete with operational subordinates should be fenced by foreign keys");
            DatabaseAssert.True(await _Driver.Tenants.ReadAsync(tenant.Id, token).ConfigureAwait(false) != null, "Tenant should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Users.ReadByIdAsync(user.Id, token).ConfigureAwait(false) != null, "User should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Credentials.ReadByIdAsync(credential.Id, token).ConfigureAwait(false) != null, "Credential should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Fleets.ReadAsync(fleet.Id, token).ConfigureAwait(false) != null, "Fleet should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false) != null, "Vessel should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false) != null, "Captain should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Voyages.ReadAsync(voyage.Id, token).ConfigureAwait(false) != null, "Voyage should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false) != null, "Mission should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false) != null, "Dock should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Signals.ReadAsync(signal.Id, token).ConfigureAwait(false) != null, "Signal should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.Events.ReadAsync(evt.Id, token).ConfigureAwait(false) != null, "Event should still exist after fenced delete");
            DatabaseAssert.True(await _Driver.MergeEntries.ReadAsync(merge.Id, token).ConfigureAwait(false) != null, "Merge entry should still exist after fenced delete");
        }

        private async Task AssertPagedTenantEnumerationAsync<T>(Func<EnumerationQuery, Task<EnumerationResult<T>>> enumerate, string existingId, Func<string, Task<T>> createAdditional)
            where T : class
        {
            T second = await createAdditional("page-two").ConfigureAwait(false);
            string secondId = (string)second.GetType().GetProperty("Id")!.GetValue(second)!;

            EnumerationResult<T> page1 = await enumerate(new EnumerationQuery { PageNumber = 1, PageSize = 1 }).ConfigureAwait(false);
            EnumerationResult<T> page2 = await enumerate(new EnumerationQuery { PageNumber = 2, PageSize = 1 }).ConfigureAwait(false);

            DatabaseAssert.EnumerationPage(page1, 1, 1, 2, 2, 1);
            DatabaseAssert.EnumerationPage(page2, 2, 1, 2, 2, 1);
            DatabaseAssert.ContainsIds(new[] { page1.Objects[0], page2.Objects[0] }, x => (string)x!.GetType().GetProperty("Id")!.GetValue(x)!, existingId, secondId);
        }

        private async Task<OperationalGraphResult> SeedOperationalGraphAsync(DatabaseFixture fixture, CancellationToken token)
        {
            TenantMetadata tenant = await fixture.CreateTenantAsync("operational-tenant", token: token).ConfigureAwait(false);
            UserMaster user = await fixture.CreateUserAsync(tenant.Id, "operational-user", token: token).ConfigureAwait(false);
            Credential credential = await fixture.CreateCredentialAsync(tenant.Id, user.Id, "operational-credential", token: token).ConfigureAwait(false);
            Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "operational-fleet", token).ConfigureAwait(false);
            Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "operational-vessel", token).ConfigureAwait(false);
            Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "operational-captain", token).ConfigureAwait(false);
            Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "operational-voyage", token).ConfigureAwait(false);
            Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "operational-mission", token).ConfigureAwait(false);
            Dock dock = await fixture.CreateDockAsync(tenant.Id, user.Id, vessel.Id, captain.Id, token).ConfigureAwait(false);
            Signal signal = await fixture.CreateSignalAsync(tenant.Id, user.Id, captain.Id, token).ConfigureAwait(false);
            ArmadaEvent evt = await fixture.CreateEventAsync(tenant.Id, user.Id, mission.Id, voyage.Id, vessel.Id, captain.Id, token).ConfigureAwait(false);
            MergeEntry merge = await fixture.CreateMergeEntryAsync(tenant.Id, user.Id, mission.Id, vessel.Id, token).ConfigureAwait(false);

            return new OperationalGraphResult
            {
                Tenant = tenant,
                User = user,
                Credential = credential,
                Fleet = fleet,
                Vessel = vessel,
                Captain = captain,
                Voyage = voyage,
                Mission = mission,
                Dock = dock,
                Signal = signal,
                Event = evt,
                MergeEntry = merge
            };
        }
    }
}
