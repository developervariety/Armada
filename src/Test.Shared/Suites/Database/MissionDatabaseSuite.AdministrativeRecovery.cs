namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Microsoft.Data.Sqlite;
    using MySqlConnector;
    using Npgsql;
    using Microsoft.Data.SqlClient;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class MissionDatabaseSuite
    {
        private static IEnumerable<TestCaseDescriptor> AdministrativeRecoveryCases()
        {
            yield return CaseAsync("mission_administrative_recovery_saved_work_atomic_consume_reopen",
                "Saved-work recovery persists, consumes once and rolls back late writes", TestTags.Positive, async () =>
            {
                // One helper allocation: a second server helper would reset the shared database.
                using (TestDatabase store = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = store.Driver;
                    RecoveryFixture positive = await SeedRecoveryAsync(db).ConfigureAwait(false);
                    string permitId = await IssueRecoveryAsync(db, positive).ConfigureAwait(false);
                    Mission resumed = await positive.Service.ConsumeRestartPermitAsync(positive.Owner,
                        positive.Worker.Id, permitId).ConfigureAwait(false);
                    AssertEqual(positive.Worker.Id, resumed.Id);
                    using (DatabaseDriver reopened = await ReopenRecoveryDatabaseAsync(store).ConfigureAwait(false))
                    {
                        await AssertConsumedRecoveryAsync(reopened, positive, permitId).ConfigureAwait(false);
                        bool spent = false;
                        try { await positive.Service.ConsumeRestartPermitAsync(positive.Owner, positive.Worker.Id, permitId).ConfigureAwait(false); }
                        catch (InvalidOperationException) { spent = true; }
                        AssertTrue(spent, "A consumed permit cannot reset the current epoch again");
                        await AssertConsumedRecoveryAsync(reopened, positive, permitId).ConfigureAwait(false);
                    }

                    RecoveryFixture concurrent = await SeedRecoveryAsync(db).ConfigureAwait(false);
                    string concurrentPermit = await IssueRecoveryAsync(db, concurrent).ConfigureAwait(false);
                    ArmadaEvent issued = (await db.Events.ReadAsync(concurrentPermit).ConfigureAwait(false))!;
                    AdministrativeRestartPermit permit = JsonSerializer.Deserialize<AdministrativeRestartPermit>(issued.Payload!)!;
                    AdministrativeVoyageRecoveryPlan recheckedSource = await concurrent.Service.ExecuteAsync(concurrent.Owner,
                        concurrent.Voyage.Id, new AdministrativeVoyageRecoveryRequest
                        {
                            DryRun = true, SourceCommit = concurrent.PlanningCommit, HistoricalWork = concurrent.Work,
                            RestartDescription = permit.RestartDescription
                        }).ConfigureAwait(false);
                    AssertEqual(permit.SourceEvidence, recheckedSource.SourceEvidence,
                        "Real source and owner checks still bind the issued permit before the transaction race");
                    using (DatabaseDriver second = await ReopenRecoveryDatabaseAsync(store).ConfigureAwait(false))
                    await using (FleetCapacityReservation capacity = await new FleetCapacityAdmission(db, concurrent.Settings)
                        .AcquireAsync(concurrent.Vessel, concurrent.Voyage.Id).ConfigureAwait(false))
                    {
                        await capacity.VerifyOwnershipAsync().ConfigureAwait(false);
                        // Source/owner evidence is the actual service-issued immutable permit, not a fabricated verifier.
                        TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        int entered = 0;
                        async Task<bool> ConsumeAsync(DatabaseDriver driver)
                        {
                            if (Interlocked.Increment(ref entered) == 2) ready.TrySetResult(true);
                            await ready.Task.ConfigureAwait(false);
                            try
                            {
                                await driver.AdministrativeVoyageRecovery.ConsumeRestartPermitAsync(Constants.DefaultTenantId,
                                    Constants.DefaultUserId, concurrent.Worker.Id, permit,
                                    new AdministrativeVoyageRecoveryRequest
                                    {
                                        SourceCommit = permit.PlanningSourceCommit, HistoricalWork = permit.Disposition,
                                        RestartTitle = permit.RestartTitle, RestartDescription = permit.RestartDescription,
                                        CapturedSourceEvidence = permit.SourceEvidence, CapturedHistoricalEvidence = true
                                    }, capacity).ConfigureAwait(false);
                                return true;
                            }
                            catch (InvalidOperationException) { return false; }
                        }
                        bool[] results = await Task.WhenAll(Task.Run(() => ConsumeAsync(db)), Task.Run(() => ConsumeAsync(second))).ConfigureAwait(false);
                        AssertEqual(1, results.Count(success => success), "Separate real connections consume the durable permit once");
                    }
                    using (DatabaseDriver reopened = await ReopenRecoveryDatabaseAsync(store).ConfigureAwait(false))
                        await AssertConsumedRecoveryAsync(reopened, concurrent, concurrentPermit).ConfigureAwait(false);

                    RecoveryFixture rollback = await SeedRecoveryAsync(db).ConfigureAwait(false);
                    string rollbackPermit = await IssueRecoveryAsync(db, rollback).ConfigureAwait(false);
                    string before = await RecoveryRowsAsync(db, rollback).ConfigureAwait(false);
                    string trigger = "armada_recovery_" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await RecoveryTriggerAsync(store, trigger, rollback.Voyage.Id, install: true).ConfigureAwait(false);
                        bool rejected = false;
                        try { await rollback.Service.ConsumeRestartPermitAsync(rollback.Owner, rollback.Worker.Id, rollbackPermit).ConfigureAwait(false); }
                        catch (DbException ex) when (ex.Message.Contains("armada_recovery_late_rollback", StringComparison.Ordinal)) { rejected = true; }
                        AssertTrue(rejected, "The test trigger rejects the voyage write AFTER receipt insertion and mission reset");
                        using (DatabaseDriver reopened = await ReopenRecoveryDatabaseAsync(store).ConfigureAwait(false))
                        {
                            AssertEqual(before, await RecoveryRowsAsync(reopened, rollback).ConfigureAwait(false), "Every preceding transactional write rolls back");
                            AssertEqual(0, (await RecoveryEventsAsync(reopened, rollback, "mission.administrative_restart_consumed").ConfigureAwait(false)).Count);
                            AssertEqual(1, (await RecoveryEventsAsync(reopened, rollback, "mission.administrative_restart_permit_issued").ConfigureAwait(false)).Count);
                        }
                    }
                    finally { await RecoveryTriggerAsync(store, trigger, rollback.Voyage.Id, install: false).ConfigureAwait(false); }
                    await rollback.Service.ConsumeRestartPermitAsync(rollback.Owner, rollback.Worker.Id, rollbackPermit).ConfigureAwait(false);
                    using (DatabaseDriver reopened = await ReopenRecoveryDatabaseAsync(store).ConfigureAwait(false))
                        await AssertConsumedRecoveryAsync(reopened, rollback, rollbackPermit).ConfigureAwait(false);

                    foreach (RecoveryFixture fixture in new[] { positive, concurrent, rollback })
                    {
                        AssertEqual(6, (await db.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).Count);
                        AssertEqual(5, (await db.Events.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).Count);
                    }
                    AssertNull(await db.CoordinationLeases.ReadAsync(FleetCapacityAdmission.BuildLeaseName(Constants.DefaultTenantId)).ConfigureAwait(false),
                        "No capacity reservation survives the test");
                }
            });
        }

        private sealed class RecoveryFixture
        {
            public Vessel Vessel { get; set; } = null!;
            public Voyage Voyage { get; set; } = null!;
            public List<Mission> Stages { get; set; } = new List<Mission>();
            public Mission Worker => Stages[1];
            public string PlanningCommit { get; set; } = String.Empty;
            public string SavedCommit { get; set; } = String.Empty;
            public HistoricalWorkDisposition Work { get; set; } = null!;
            public AuthContext Owner { get; set; } = null!;
            public ArmadaSettings Settings { get; set; } = null!;
            public AdministrativeVoyageRecoveryService Service { get; set; } = null!;
        }

        private static async Task<RecoveryFixture> SeedRecoveryAsync(DatabaseDriver db)
        {
            MissionTestPrerequisites prerequisites = await CreatePrerequisitesAsync(db, " " + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
            string repository = TestGitRepoHelper.CreateWorkingRepoCopy();
            string remote = TestGitRepoHelper.CreateBareRepoCopy();
            await RecoveryGitAsync(repository, "remote", "add", "origin", remote).ConfigureAwait(false);
            await RecoveryGitAsync(repository, "fetch", "origin").ConfigureAwait(false);
            string planner = (await RecoveryGitAsync(repository, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await RecoveryGitAsync(repository, "branch", "planning", planner).ConfigureAwait(false);
            await RecoveryGitAsync(repository, "checkout", "-b", "saved-work").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repository, "saved-work.txt"), "Unaccepted source retained by the test. é🙂").ConfigureAwait(false);
            await RecoveryGitAsync(repository, "add", "saved-work.txt").ConfigureAwait(false);
            await RecoveryGitAsync(repository, "commit", "-m", "Retain unaccepted work").ConfigureAwait(false);
            string saved = (await RecoveryGitAsync(repository, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await RecoveryGitAsync(repository, "branch", "recover/saved-work", saved).ConfigureAwait(false);
            await RecoveryGitAsync(repository, "checkout", "main").ConfigureAwait(false);
            Vessel vessel = prerequisites.Vessel;
            vessel.TenantId = Constants.DefaultTenantId; vessel.UserId = Constants.DefaultUserId;
            vessel.LocalPath = repository; vessel.DefaultBranch = "main";
            await db.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
            Voyage voyage = prerequisites.Voyage;
            voyage.TenantId = Constants.DefaultTenantId; voyage.UserId = Constants.DefaultUserId;
            voyage.Status = VoyageStatusEnum.Complete; voyage.CompletedUtc = _BaseTime.AddTicks(1234567);
            await db.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
            RecoveryFixture fixture = new RecoveryFixture { Vessel = vessel, PlanningCommit = planner, SavedCommit = saved };
            string chain = Mission.NewStageChainId();
            foreach (string role in new[] { "Architect", "Worker", "TestEngineer", "Linter", "Worker", "Judge" })
            {
                int index = fixture.Stages.Count;
                Mission mission = await db.Missions.CreateAsync(new Mission("Required " + role, "Retain this independent duty. é🙂")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                    Persona = role, Mode = MissionModeEnum.Implementation, StageChainId = chain, StageOrder = index + 1,
                    DependsOnMissionId = fixture.Stages.LastOrDefault()?.Id,
                    Status = index == 0 ? MissionStatusEnum.Complete : MissionStatusEnum.Cancelled,
                    StartFromRef = planner, CommitHash = index == 0 ? planner : null, BranchName = index == 0 ? "planning" : null,
                    CreatedUtc = _BaseTime.AddTicks(1234567 + index),
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
                    StartedUtc = index == 1 ? DateTime.UtcNow.AddMinutes(-5) : null,
                    AgentOutput = index == 1 ? "Retained unaccepted output. é🙂" : null,
                    FailureReason = index == 0 ? null : "Mission cancelled by operator.",
                    RecoveryAttempts = index == 1 ? 3 : 0, LandingRetryCount = index == 1 ? 2 : 0, RequiresReview = true,
                    ReviewDenyAction = ReviewDenyActionEnum.FailPipeline
                }).ConfigureAwait(false);
                // Provider precision is established by an actual read, never the unrounded input object.
                fixture.Stages.Add((await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!);
            }
            fixture.Voyage = (await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!;
            ArmadaEvent cancellation = await db.Events.CreateAsync(new ArmadaEvent("mission.cancelled", "Mission cancelled by operator.")
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, EntityType = "mission", EntityId = fixture.Worker.Id,
                MissionId = fixture.Worker.Id, VesselId = vessel.Id, VoyageId = voyage.Id,
                CreatedUtc = fixture.Worker.CompletedUtc!.Value.AddSeconds(1)
            }).ConfigureAwait(false);
            await db.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, MissionId = fixture.Worker.Id,
                RootMissionId = fixture.Worker.Id, VoyageId = voyage.Id, VesselId = vessel.Id, FactType = MissionAttemptFactTypeEnum.Restarted
            }).ConfigureAwait(false);
            ArmadaEvent storedCancellation = (await db.Events.ReadAsync(cancellation.Id).ConfigureAwait(false))!;
            fixture.Work = new HistoricalWorkDisposition
            {
                Kind = HistoricalWorkDispositionKindEnum.Unaccepted, MissionId = fixture.Worker.Id,
                PreservedSourceRef = "recover/saved-work", PreservedSourceCommit = saved, CancellationEventId = cancellation.Id,
                NativeEvidenceSHA256 = RecoveryDigest(JsonSerializer.Serialize(new { storedCancellation, fixture.Worker.AgentOutput })),
                CountsEvidenceSHA256 = RecoveryDigest(JsonSerializer.Serialize(new { fixture.Worker.RecoveryAttempts, fixture.Worker.LandingRetryCount })),
                Reason = "The test's retained source and output remain unaccepted; preserve their history."
            };
            fixture.Owner = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "test");
            fixture.Settings = new ArmadaSettings();
            fixture.Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 100;
            fixture.Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 100;
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            GitService git = new GitService(logging);
            DockService docks = new DockService(logging, db, fixture.Settings, git);
            CaptainService captains = new CaptainService(logging, db, fixture.Settings, git, docks);
            MissionService missions = new MissionService(logging, db, fixture.Settings, docks, captains);
            // Construct the real owner without starting its scheduler or any captain.
            AdmiralService admiral = new AdmiralService(logging, db, fixture.Settings, captains, missions, new VoyageService(logging, db), docks);
            fixture.Service = new AdministrativeVoyageRecoveryService(db, admiral, git, fixture.Settings);
            // Only the authenticated operator owner can enable this server-owned field.
            await missions.UpdateAutomaticWorkPermissionAsync(fixture.Owner, fixture.Worker.Id,
                new MissionAutomaticWorkPermissionRequest
                {
                    Operation = MissionAutomaticPermissionOperationEnum.Enable,
                    Reason = "Keep automatic verification and dependents held during saved-work recovery."
                }).ConfigureAwait(false);
            fixture.Stages[1] = (await db.Missions.ReadAsync(fixture.Worker.Id).ConfigureAwait(false))!;
            AssertNotNull(fixture.Worker.AutomaticWorkPermission, "The operator enabled the hold before the recovery preview");
            return fixture;
        }

        private static async Task<string> IssueRecoveryAsync(DatabaseDriver db, RecoveryFixture fixture)
        {
            AdministrativeVoyageRecoveryRequest request = new AdministrativeVoyageRecoveryRequest
            { SourceCommit = fixture.PlanningCommit, HistoricalWork = fixture.Work, RestartDescription = "Continue the retained work and all required reviews." };
            AdministrativeVoyageRecoveryPlan preview = await fixture.Service.ExecuteAsync(fixture.Owner, fixture.Voyage.Id, request).ConfigureAwait(false);
            fixture.Work.GraphFingerprint = preview.Fingerprint;
            preview = await fixture.Service.ExecuteAsync(fixture.Owner, fixture.Voyage.Id, request).ConfigureAwait(false);
            AssertEqual(0, preview.Blockers.Count, String.Join(",", preview.Blockers));
            string before = await RecoveryRowsAsync(db, fixture).ConfigureAwait(false);
            request.DryRun = false; request.ExpectedFingerprint = new string('0', 64);
            bool stale = false;
            try { await fixture.Service.ExecuteAsync(fixture.Owner, fixture.Voyage.Id, request).ConfigureAwait(false); }
            catch (InvalidOperationException) { stale = true; }
            AssertTrue(stale); AssertEqual(before, await RecoveryRowsAsync(db, fixture).ConfigureAwait(false));
            AssertEqual(0, (await RecoveryEventsAsync(db, fixture, "voyage.administrative_completion_corrected").ConfigureAwait(false)).Count);
            request.ExpectedFingerprint = preview.Fingerprint;
            AdministrativeVoyageRecoveryPlan result = await fixture.Service.ExecuteAsync(fixture.Owner, fixture.Voyage.Id, request).ConfigureAwait(false);
            AssertTrue(result.Applied); AssertTrue(!String.IsNullOrEmpty(result.RestartPermitId));
            foreach (Mission original in fixture.Stages)
                AssertEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await db.Missions.ReadAsync(original.Id).ConfigureAwait(false)), "Issuing a permit starts no stage");
            Voyage corrected = (await db.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!;
            AssertEqual(VoyageStatusEnum.Failed, corrected.Status); AssertEqual(fixture.Voyage.CompletedUtc, corrected.CompletedUtc);
            return result.RestartPermitId!;
        }

        private static async Task AssertConsumedRecoveryAsync(DatabaseDriver db, RecoveryFixture fixture, string permitId)
        {
            Mission current = (await db.Missions.ReadAsync(fixture.Worker.Id).ConfigureAwait(false))!;
            AssertEqual(MissionStatusEnum.Pending, current.Status); AssertEqual(fixture.SavedCommit, current.StartFromRef);
            AssertEqual(fixture.Worker.CreatedUtc, current.CreatedUtc); AssertEqual(fixture.Worker.RecoveryAttempts, current.RecoveryAttempts);
            AssertEqual(fixture.Worker.LandingRetryCount, current.LandingRetryCount); AssertEqual(fixture.Worker.DependsOnMissionId, current.DependsOnMissionId);
            AssertNull(current.ProcessId); AssertNull(current.StartedUtc); AssertNull(current.CompletedUtc);
            AssertNotNull(current.AutomaticWorkPermission, "Recovery preserves the explicit per-mission hold");
            AssertTrue(!MissionAutomaticWorkPermissionPolicy.IsAllowed(current, MissionAutomaticActionEnum.Verification,
                DateTime.UtcNow, out string? verificationReason));
            AssertEqual("automatic_verification_permission_required", verificationReason);
            AssertTrue(!MissionAutomaticWorkPermissionPolicy.IsAllowed(current, MissionAutomaticActionEnum.DependentAdmission,
                DateTime.UtcNow, out string? dependentReason));
            AssertEqual("dependent_admission_permission_required", dependentReason);
            AssertEqual(VoyageStatusEnum.InProgress, (await db.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!.Status);
            foreach (Mission original in fixture.Stages.Where(m => m.Id != fixture.Worker.Id))
                AssertEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await db.Missions.ReadAsync(original.Id).ConfigureAwait(false)));
            List<ArmadaEvent> events = await RecoveryEventsAsync(db, fixture, "mission.administrative_restart_consumed").ConfigureAwait(false);
            AssertEqual(1, events.Count); AssertEqual(fixture.Vessel.Id, events[0].VesselId);
            EnumerationResult<ArmadaEvent> byVessel = await db.Events.EnumerateAsync(new EnumerationQuery
            { VesselId = fixture.Vessel.Id, EventType = "mission.administrative_restart_consumed", PageSize = 10 }).ConfigureAwait(false);
            AssertEqual(1, byVessel.Objects.Count); AssertEqual(events[0].Id, byVessel.Objects[0].Id);
            AdministrativeRestartReceipt receipt = JsonSerializer.Deserialize<AdministrativeRestartReceipt>(events[0].Payload!)!;
            AssertEqual(permitId, receipt.PermitId); AssertEqual(fixture.Worker.CreatedUtc, receipt.OriginalCreatedUtc);
            AssertEqual(fixture.PlanningCommit, receipt.DependencyCommit); AssertEqual(fixture.SavedCommit, receipt.SourceCommit);
            using (JsonDocument prior = JsonDocument.Parse(receipt.PriorSnapshot))
            {
                JsonElement priorWorker = prior.RootElement.GetProperty("missions").EnumerateArray()
                    .Single(row => row.GetProperty("id").GetString() == fixture.Worker.Id);
                AssertEqual(fixture.Worker.AgentOutput, priorWorker.GetProperty("agent_output").GetString(),
                    "The archive preserves decoded Unicode output, not merely its escaped JSON spelling");
            }
            AssertEqual(fixture.SavedCommit, await AdministrativeRestartSource.ResolveAsync(db, current, fixture.Stages[0]).ConfigureAwait(false));
            ProductionFactPage<MissionAttemptFact> facts = await db.MissionAttemptFacts.EnumerateByMissionAsync(
                Constants.DefaultTenantId, fixture.Worker.Id).ConfigureAwait(false);
            AssertTrue(!facts.Truncated);
            AssertEqual(2, facts.Items.Count); AssertEqual(1, facts.Items.Count(f => f.ReasonCode == "administrative_restart_permit"));
        }

        private static async Task<string> RecoveryRowsAsync(DatabaseDriver db, RecoveryFixture fixture)
        {
            List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                Voyage = await db.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false),
                Missions = missions.OrderBy(m => m.Id, StringComparer.Ordinal),
                Facts = await db.MissionAttemptFacts.EnumerateByMissionAsync(Constants.DefaultTenantId, fixture.Worker.Id).ConfigureAwait(false),
                Events = (await db.Events.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).OrderBy(e => e.Id, StringComparer.Ordinal)
            });
        }
        private static async Task<List<ArmadaEvent>> RecoveryEventsAsync(DatabaseDriver db, RecoveryFixture fixture, string type)
            => (await db.Events.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).Where(e => e.EventType == type).ToList();
        private static string RecoveryDigest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        private static async Task<DatabaseDriver> ReopenRecoveryDatabaseAsync(TestDatabase store)
        {
            LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
            DatabaseDriver driver = TestDatabaseConfig.IsSqlite ? new SqliteDatabaseDriver(store.ConnectionString, logging)
                : DatabaseDriverFactory.Create(TestDatabaseConfig.BuildSettings(TestDatabaseConfig.BaseDatabaseName + "_shared"), logging);
            await driver.InitializeAsync().ConfigureAwait(false); return driver;
        }
        private static async Task<string> RecoveryGitAsync(string root, params string[] args)
        {
            ProcessStartInfo start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using (Process process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start"))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); throw; }
                }
                string stdout = await output.ConfigureAwait(false); string stderr = await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Owned fixture Git failed: " + stderr);
                return stdout;
            }
        }
        private static async Task RecoveryTriggerAsync(TestDatabase store, string name, string voyageId, bool install)
        {
            AssertTrue(name.All(c => Char.IsAsciiLetterOrDigit(c) || c == '_') && voyageId.All(c => Char.IsAsciiLetterOrDigit(c) || c == '_'));
            using (DbConnection connection = TestDatabaseConfig.Type switch
            {
                DatabaseTypeEnum.Sqlite => new SqliteConnection(store.ConnectionString),
                DatabaseTypeEnum.Postgresql => new NpgsqlConnection(store.ConnectionString),
                DatabaseTypeEnum.Mysql => new MySqlConnection(store.ConnectionString),
                DatabaseTypeEnum.SqlServer => new SqlConnection(store.ConnectionString),
                _ => throw new NotSupportedException("Unknown test provider")
            })
            {
                await connection.OpenAsync().ConfigureAwait(false);
                string sql;
                if (!install)
                {
                    sql = "DROP TRIGGER IF EXISTS " + name + (TestDatabaseConfig.Type == DatabaseTypeEnum.Postgresql ? " ON voyages; DROP FUNCTION IF EXISTS " + name + "();" : ";");
                }
                else
                {
                    string reject = "NEW.id='" + voyageId + "' AND NEW.status='InProgress'";
                    sql = TestDatabaseConfig.Type switch
                    {
                        DatabaseTypeEnum.Sqlite => "CREATE TRIGGER " + name + " BEFORE UPDATE OF status ON voyages WHEN " + reject + " BEGIN SELECT RAISE(ABORT,'armada_recovery_late_rollback'); END;",
                        DatabaseTypeEnum.Postgresql => "CREATE FUNCTION " + name + "() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF " + reject + " THEN RAISE EXCEPTION 'armada_recovery_late_rollback'; END IF; RETURN NEW; END; $$; CREATE TRIGGER " + name + " BEFORE UPDATE ON voyages FOR EACH ROW EXECUTE FUNCTION " + name + "();",
                        DatabaseTypeEnum.Mysql => "CREATE TRIGGER " + name + " BEFORE UPDATE ON voyages FOR EACH ROW BEGIN IF " + reject + " THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='armada_recovery_late_rollback'; END IF; END;",
                        DatabaseTypeEnum.SqlServer => "CREATE TRIGGER " + name + " ON voyages AFTER UPDATE AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE id='" + voyageId + "' AND status='InProgress') THROW 50001,'armada_recovery_late_rollback',1; END;",
                        _ => throw new NotSupportedException("Unknown test provider")
                    };
                }
                using (DbCommand command = connection.CreateCommand()) { command.CommandText = sql; await command.ExecuteNonQueryAsync().ConfigureAwait(false); }
            }
        }
    }
}
