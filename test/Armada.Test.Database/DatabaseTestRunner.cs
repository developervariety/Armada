#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
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

            await RunTest("ModelEndpoint_Persistence_Scope_Unicode_Reopen", "Operational", () => TestModelEndpointPersistenceAsync(token), token);
            await RunTest("Captain_ModelEndpoint_Link_Persists_Across_Reopen", "Operational", () => TestCaptainModelEndpointLinkAsync(token), token);
            await RunTest("ModelEndpoint_Health_Conditional_Update_CAS_And_Nulls", "Operational", () => TestModelEndpointHealthCasAsync(token), token);
            await RunTest("ModelEndpoint_Persistence_Rejects_Corrupt_Enums", "Operational", () => TestModelEndpointCorruptEnumsAsync(token), token);

            if (_Settings.Type == Armada.Core.Enums.DatabaseTypeEnum.Mysql)
                await RunTest("MySQL_Unicode_Full_Uniqueness_Concurrency_Rollback", "Schema", () => new MysqlUnicodeUniquenessTests(_Settings).VerifyAsync(token), token);

            await RunTest("CoordinationLease_Reopen_Ownership_Expiry", "Operational", () => TestCoordinationLeaseAsync(token), token);
            await RunTest("HarborRunnerEnrollment_Reopen_And_CAS_Race", "Operational", () => new HarborRunnerEnrollmentDatabaseTests(_Driver, _Settings).VerifyAsync(token), token);
            await RunTest("Objective_Terminal_Backlog_Migration_Repairs_Only_Terminal_Rows", "Operational", () => TestObjectiveTerminalBacklogMigrationAsync(token), token);
            await RunTest("MissionAttemptFacts_Window_Scope_Bound_Reopen", "Operational", () => new ProductionFactDatabaseTests(_Driver, _Settings).VerifyMissionAttemptFactsAsync(token), token);
            await RunTest("CheckRun_Regression_Links_Create_Update_Reopen", "Operational", () => new ProductionFactDatabaseTests(_Driver, _Settings).VerifyCheckRegressionLinksAsync(token), token);

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
            await RunTest("Mission_Fork_Fields_Create_Update_Reopen_Query", "Operational", () => TestMissionForkFieldsAsync(token), token);
            await RunTest("Dock_Create_Read_Update", "Operational", () => TestDockCrudAsync(token), token);
            await RunTest("Signal_Create_Read_Enumerate_MarkRead", "Operational", () => TestSignalCrudAsync(token), token);
            await RunTest("Signal_EnumerateRecent_Recipient_Unread", "Operational", () => TestSignalLookupAsync(token), token);
            await RunTest("Event_Create_Read_Enumerate", "Operational", () => TestEventCrudAsync(token), token);
            await RunTest("Event_FilteredEnumerations", "Operational", () => TestEventLookupAsync(token), token);
            await RunTest("MergeEntry_Create_Read_Update_Enumerate", "Operational", () => TestMergeEntryCrudAsync(token), token);
            await RunTest("MergeEntry_EnumerateByStatus_Exists", "Operational", () => TestMergeEntryLookupAsync(token), token);
            await RunTest("WorkflowProfile_Create_Read_Update_Enumerate", "Operational", () => TestWorkflowProfileCrudAsync(token), token);
            await RunTest("CheckRun_Create_Read_Update_Enumerate", "Operational", () => TestCheckRunCrudAsync(token), token);
            await RunTest("Environment_Create_Read_Update_Enumerate", "Operational", () => TestEnvironmentCrudAsync(token), token);
            await RunTest("Release_Create_Read_Update_Enumerate", "Operational", () => TestReleaseCrudAsync(token), token);
            await RunTest("Deployment_Create_Read_Update_Enumerate", "Operational", () => TestDeploymentCrudAsync(token), token);
            await RunTest("Objective_Create_Read_Update_Enumerate", "Operational", () => TestObjectiveCrudAsync(token), token);
            await RunTest("ObjectiveRefinementSession_Message_Create_Read_Update_Enumerate", "Operational", () => TestObjectiveRefinementCrudAsync(token), token);
            await RunTest("Memory_Create_Read_Update_Tags_Reopen", "Operational", () => TestMemoryCrudAsync(token), token);
            await RunTest("Memory_Tenant_Fence_Key_Uniqueness_Guarded_Update", "Operational", () => TestMemoryScopingAsync(token), token);

            Console.WriteLine();
            Console.WriteLine("--- Cascade Verification ---");
            await RunTest("Objective_ForeignKeys_And_Refinement_Cascade", "Cascade", () => TestObjectiveForeignKeysAsync(token), token);
            await RunTest("Tenant_Delete_Cascades_Auth_Data", "Cascade", () => TestTenantAuthCascadeDeleteAsync(token), token);
            await RunTest("Tenant_Delete_With_Operational_Subordinates_Is_FK_Fenced", "Cascade", () => TestTenantDeleteFencedByOperationalDataAsync(token), token);

            MultiTenantScopingTests scopingTests = new MultiTenantScopingTests(_Driver, _NoCleanup);
            List<TestResult> scopingResults = await scopingTests.RunAllAsync(token).ConfigureAwait(false);
            _Results.AddRange(scopingResults);

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
                catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid model endpoint " + field, StringComparison.Ordinal))
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
                DatabaseAssert.True(await _Driver.Captains.TryClaimAsync(claimed.Id, graph.Mission.Id, claimedDock.Id, token).ConfigureAwait(false), "Claim the captain");
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
                DatabaseAssert.Equal(captain.Id, read.ToCaptainId, "Signal.ToCaptainId");
                DatabaseAssert.Equal(false, read.Read, "Signal.Read");

                EnumerationResult<Signal> page = await _Driver.Signals.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 10 }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 1, "Signal enumeration should include created signal");
                DatabaseAssert.ContainsIds(page.Objects, x => x.Id, signal.Id);

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
                DatabaseAssert.Equal(vessel.Id, read.VesselId, "Event.VesselId");
                DatabaseAssert.Equal(captain.Id, read.CaptainId, "Event.CaptainId");
                DatabaseAssert.Equal(mission.Id, read.MissionId, "Event.MissionId");
                DatabaseAssert.Equal(voyage.Id, read.VoyageId, "Event.VoyageId");

                EnumerationResult<ArmadaEvent> page = await _Driver.Events.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 10, MissionId = mission.Id }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 1, "Event enumeration should include created event");
                DatabaseAssert.ContainsIds(page.Objects, x => x.Id, evt.Id);
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

                read.Status = MergeStatusEnum.Landed;
                MergeEntry updated = await _Driver.MergeEntries.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal(MergeStatusEnum.Landed, updated.Status, "MergeEntry.Status");

                EnumerationResult<MergeEntry> page = await _Driver.MergeEntries.EnumerateAsync(tenant.Id, new EnumerationQuery { PageNumber = 1, PageSize = 10, MissionId = mission.Id }, token).ConfigureAwait(false);
                DatabaseAssert.True(page.TotalRecords >= 1, "Merge entry enumeration should include created merge entry");
                DatabaseAssert.ContainsIds(page.Objects, x => x.Id, merge.Id);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
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
                WorkflowProfile updated = await _Driver.WorkflowProfiles.UpdateAsync(read, token).ConfigureAwait(false);
                DatabaseAssert.Equal("Updated workflow profile description", updated.Description, "Updated WorkflowProfile.Description");
                DatabaseAssert.Equal("dotnet build -c Release", updated.BuildCommand, "Updated WorkflowProfile.BuildCommand");
                DatabaseAssert.True(updated.ExpectedArtifacts.Contains("artifacts/extra.zip"), "Updated WorkflowProfile.ExpectedArtifacts");

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

        private async Task TestCheckRunCrudAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                OperationalGraphResult graph = await SeedOperationalGraphAsync(fixture, token).ConfigureAwait(false);
                WorkflowProfile profile = await fixture.CreateWorkflowProfileAsync(graph.Tenant.Id, graph.User.Id, "checkrun-profile", graph.Fleet.Id, graph.Vessel.Id, token).ConfigureAwait(false);
                CheckRun runA = await fixture.CreateCheckRunAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, graph.Mission.Id, graph.Voyage.Id, token).ConfigureAwait(false);
                CheckRun runB = await fixture.CreateCheckRunAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, graph.Mission.Id, graph.Voyage.Id, token).ConfigureAwait(false);

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
                Release releaseA = await fixture.CreateReleaseAsync(
                    graph.Tenant.Id,
                    graph.User.Id,
                    graph.Vessel.Id,
                    profile.Id,
                    new[] { graph.Voyage.Id },
                    new[] { graph.Mission.Id },
                    new[] { checkRun.Id },
                    token).ConfigureAwait(false);
                Release releaseB = await fixture.CreateReleaseAsync(graph.Tenant.Id, graph.User.Id, graph.Vessel.Id, profile.Id, null, null, null, token).ConfigureAwait(false);

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
                    token).ConfigureAwait(false);
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
                    token).ConfigureAwait(false);

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
                }

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
