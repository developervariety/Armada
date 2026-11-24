namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>Operator authorization, independent actions and atomic audit through the public mission service.</summary>
    public sealed class MissionAutomaticWorkPermissionOperatorTests : TestSuite
    {
        private const string _OperatorId = "permission-operator";
        private const string _EventType = "mission.automatic_work_permission_changed";

        /// <inheritdoc />
        public override string Name => "Mission Automatic Work Permission Operator";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Unauthenticated captain and Ask callers cannot change or audit permissions", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    AuthContext captain = Operator();
                    captain.MissionId = mission.Id;
                    AuthContext askThread = Operator();
                    askThread.AskThreadId = "permission-thread";
                    AuthContext askTurn = Operator();
                    askTurn.AskTurnId = "permission-turn";
                    AuthContext noUser = Operator();
                    noUser.UserId = null;
                    foreach (AuthContext caller in new[] { new AuthContext(), captain, askThread, askTurn, noUser })
                    {
                        await AssertRejectedAsync<UnauthorizedAccessException>(db, service, caller, mission.Id, Enable()).ConfigureAwait(false);
                    }
                    await AssertRejectedAsync<UnauthorizedAccessException>(db, service, null!, mission.Id, Enable()).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("Foreign tenant and same-tenant nonowner see no mission and cause no write", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    string before = JsonSerializer.Serialize(mission);
                    foreach (AuthContext caller in new[]
                    {
                        AuthContext.Authenticated("other-tenant", "other-owner", false, true, "Test"),
                        AuthContext.Authenticated(Constants.DefaultTenantId, "other-owner", false, false, "Test")
                    })
                    {
                        AssertNull(await service.UpdateAutomaticWorkPermissionAsync(caller, mission.Id, Enable()).ConfigureAwait(false));
                        AssertEqual(before, JsonSerializer.Serialize(await ReadAsync(db, mission.Id).ConfigureAwait(false)));
                        AssertEqual(0, (await AuditsAsync(db, mission.Id).ConfigureAwait(false)).Count);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("A pending mission can opt in without a grant and repeated Enable keeps both grants", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission pending = await CreateMissionAsync(db, MissionStatusEnum.Pending).ConfigureAwait(false);
                    pending.StartedUtc = null;
                    pending.CommitHash = null;
                    await db.Driver.Missions.UpdateAsync(pending).ConfigureAwait(false);
                    pending = await ReadAsync(db, pending.Id).ConfigureAwait(false);
                    Mission enabled = await ApplyAsync(db, service, pending.Id, Enable()).ConfigureAwait(false);
                    AssertNotNull(enabled.AutomaticWorkPermission);
                    AssertNull(enabled.AutomaticWorkPermission!.Verification);
                    AssertNull(enabled.AutomaticWorkPermission.DependentAdmission);
                    AssertEqual(State(pending), State(enabled));
                    Mission repeated = await ApplyAsync(db, service, pending.Id, Enable()).ConfigureAwait(false);
                    AssertEqual(JsonSerializer.Serialize(enabled), JsonSerializer.Serialize(repeated));
                    AssertEqual(1, (await AuditsAsync(db, pending.Id).ConfigureAwait(false)).Count);

                    Mission ready = await CreateMissionAsync(db).ConfigureAwait(false);
                    await ApplyAsync(db, service, ready.Id, Enable()).ConfigureAwait(false);
                    await ApplyAsync(db, service, ready.Id, Grant(ready, MissionAutomaticActionEnum.Verification)).ConfigureAwait(false);
                    Mission both = await ApplyAsync(db, service, ready.Id, Grant(ready, MissionAutomaticActionEnum.DependentAdmission)).ConfigureAwait(false);
                    Mission after = await ApplyAsync(db, service, ready.Id, Enable()).ConfigureAwait(false);
                    AssertEqual(JsonSerializer.Serialize(both), JsonSerializer.Serialize(after));
                    AssertEqual(3, (await AuditsAsync(db, ready.Id).ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);

            await RunTest("Grant binds the current launch source action and authenticated issuer and audits the change", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Enable()).ConfigureAwait(false);
                    DateTime before = DateTime.UtcNow;
                    MissionAutomaticWorkPermissionRequest request = Grant(mission, MissionAutomaticActionEnum.Verification);
                    request.ExpiresUtc = before.AddHours(1);
                    Mission granted = await ApplyAsync(db, service, mission.Id, request).ConfigureAwait(false);
                    MissionAutomaticWorkGrant actual = granted.AutomaticWorkPermission!.Verification!;
                    AssertEqual(mission.Id, actual.MissionId);
                    AssertEqual(mission.TenantId, actual.TenantId);
                    AssertEqual(mission.UserId, actual.UserId);
                    AssertEqual(mission.StartedUtc!.Value, actual.StartedUtc);
                    AssertEqual(mission.CommitHash, actual.CommitHash);
                    AssertEqual(_OperatorId, actual.GrantedByUserId);
                    AssertTrue(actual.GrantedUtc >= before && actual.GrantedUtc <= DateTime.UtcNow);
                    AssertEqual(request.ExpiresUtc!.Value, actual.ExpiresUtc);
                    AssertTrue(actual.ExpiresUtc <= actual.GrantedUtc.AddHours(1));
                    AssertNull(granted.AutomaticWorkPermission.DependentAdmission);
                    AssertEqual(State(mission), State(granted));

                    List<ArmadaEvent> events = await AuditsAsync(db, mission.Id).ConfigureAwait(false);
                    AssertEqual(2, events.Count);
                    ArmadaEvent audit = events.Single(item => ReadAudit(item).Operation == MissionAutomaticPermissionOperationEnum.Grant);
                    PermissionAudit payload = ReadAudit(audit);
                    AssertEqual(MissionAutomaticActionEnum.Verification, payload.Action!.Value);
                    AssertEqual(_OperatorId, payload.ActorUserId);
                    AssertEqual(request.Reason, payload.Reason);
                    AssertEqual(request.ExpectedStartedUtc, payload.ExpectedStartedUtc);
                    AssertEqual(request.ExpectedCommitHash, payload.ExpectedCommitHash);
                    AssertEqual(request.ExpiresUtc, payload.ExpiresUtc);
                    AssertEqual(1L, payload.ExpectedRevision);
                    AssertEqual(2L, payload.NewRevision);
                    AssertEqual(mission.Id, audit.MissionId);
                    AssertEqual(mission.Id, audit.EntityId);
                    AssertEqual(mission.TenantId, audit.TenantId);
                    AssertEqual(mission.UserId, audit.UserId);
                }
            }).ConfigureAwait(false);

            await RunTest("Dependent grants remain idempotent and available for completed and pull-request producers", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Enable()).ConfigureAwait(false);
                    MissionAutomaticWorkPermissionRequest request = Grant(mission, MissionAutomaticActionEnum.DependentAdmission);
                    Mission first = await ApplyAsync(db, service, mission.Id, request).ConfigureAwait(false);
                    Mission repeated = await ApplyAsync(db, service, mission.Id, request).ConfigureAwait(false);
                    AssertEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
                    AssertEqual(first.AutomaticWorkPermissionWriteRevision, repeated.AutomaticWorkPermissionWriteRevision);
                    AssertEqual(first.AutomaticWorkPermissionRawJson, repeated.AutomaticWorkPermissionRawJson);
                    AssertEqual(2, (await AuditsAsync(db, mission.Id).ConfigureAwait(false)).Count);

                    foreach (MissionStatusEnum completedStatus in new[] { MissionStatusEnum.Complete, MissionStatusEnum.PullRequestOpen })
                    {
                        Mission producer = await CreateMissionAsync(db).ConfigureAwait(false);
                        await ApplyAsync(db, service, producer.Id, Enable()).ConfigureAwait(false);
                        Mission verified = await ApplyAsync(db, service, producer.Id,
                            Grant(producer, MissionAutomaticActionEnum.Verification)).ConfigureAwait(false);
                        string verification = JsonSerializer.Serialize(verified.AutomaticWorkPermission!.Verification);

                        // Set up a dependency-satisfying producer. The public permission owner must admit its
                        // dependent without a raw status rewind or a different launch/source identity.
                        producer = await ReadAsync(db, producer.Id).ConfigureAwait(false);
                        producer.Status = completedStatus;
                        producer.CompletedUtc = DateTime.UtcNow;
                        producer.ReviewedUtc = producer.CompletedUtc;
                        producer.ReviewedByUserId = _OperatorId;
                        await db.Driver.Missions.UpdateAsync(producer).ConfigureAwait(false);
                        producer = await ReadAsync(db, producer.Id).ConfigureAwait(false);
                        request = Grant(producer, MissionAutomaticActionEnum.DependentAdmission);
                        Mission admitted = await ApplyAsync(db, service, producer.Id, request).ConfigureAwait(false);
                        AssertEqual(completedStatus, admitted.Status);
                        AssertEqual(producer.StartedUtc, admitted.StartedUtc);
                        AssertEqual(producer.CommitHash, admitted.CommitHash);
                        AssertEqual(verification, JsonSerializer.Serialize(admitted.AutomaticWorkPermission!.Verification));
                        MissionAutomaticWorkGrant dependent = admitted.AutomaticWorkPermission.DependentAdmission!;
                        AssertNotNull(dependent);
                        AssertEqual(producer.Id, dependent.MissionId);
                        AssertEqual(producer.StartedUtc!.Value, dependent.StartedUtc);
                        AssertEqual(producer.CommitHash, dependent.CommitHash);
                        AssertEqual(_OperatorId, dependent.GrantedByUserId);
                        AssertEqual(request.ExpiresUtc!.Value, dependent.ExpiresUtc);
                        Mission admittedAgain = await ApplyAsync(db, service, producer.Id, request).ConfigureAwait(false);
                        AssertEqual(JsonSerializer.Serialize(admitted), JsonSerializer.Serialize(admittedAgain));

                        List<ArmadaEvent> completedAudits = await AuditsAsync(db, producer.Id).ConfigureAwait(false);
                        AssertEqual(3, completedAudits.Count);
                        ArmadaEvent completedGrant = completedAudits.Single(item =>
                            ReadAudit(item).Action == MissionAutomaticActionEnum.DependentAdmission);
                        PermissionAudit payload = ReadAudit(completedGrant);
                        AssertEqual(MissionAutomaticPermissionOperationEnum.Grant, payload.Operation!.Value);
                        AssertEqual(_OperatorId, payload.ActorUserId);
                        AssertEqual(request.Reason, payload.Reason);
                        AssertEqual(producer.StartedUtc, payload.ExpectedStartedUtc);
                        AssertEqual(producer.CommitHash, payload.ExpectedCommitHash);
                        AssertEqual(request.ExpiresUtc, payload.ExpiresUtc);
                        AssertEqual(producer.Id, completedGrant.MissionId);
                        AssertEqual(producer.TenantId, completedGrant.TenantId);
                        AssertEqual(producer.UserId, completedGrant.UserId);

                        await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), producer.Id,
                            Grant(producer, MissionAutomaticActionEnum.Verification), "automatic_work_permission_source_not_ready:").ConfigureAwait(false);
                        MissionAutomaticWorkPermissionRequest staleAttempt = Grant(producer, MissionAutomaticActionEnum.DependentAdmission);
                        staleAttempt.ExpectedStartedUtc = producer.StartedUtc!.Value.AddSeconds(-1);
                        await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), producer.Id,
                            staleAttempt, "automatic_work_permission_binding_changed:").ConfigureAwait(false);
                        MissionAutomaticWorkPermissionRequest staleSource = Grant(producer, MissionAutomaticActionEnum.DependentAdmission);
                        staleSource.ExpectedCommitHash = new string('b', 40);
                        await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), producer.Id,
                            staleSource, "automatic_work_permission_binding_changed:").ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Revoke affects only the selected action is idempotent and refuses a different attempt or source", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Enable()).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Grant(mission, MissionAutomaticActionEnum.Verification)).ConfigureAwait(false);
                    Mission both = await ApplyAsync(db, service, mission.Id, Grant(mission, MissionAutomaticActionEnum.DependentAdmission)).ConfigureAwait(false);
                    MissionAutomaticWorkPermissionRequest request = Revoke(mission, MissionAutomaticActionEnum.Verification);
                    Mission revoked = await ApplyAsync(db, service, mission.Id, request).ConfigureAwait(false);
                    AssertNull(revoked.AutomaticWorkPermission!.Verification);
                    AssertEqual(JsonSerializer.Serialize(both.AutomaticWorkPermission!.DependentAdmission),
                        JsonSerializer.Serialize(revoked.AutomaticWorkPermission.DependentAdmission));
                    AssertEqual(State(mission), State(revoked));
                    Mission repeated = await ApplyAsync(db, service, mission.Id, request).ConfigureAwait(false);
                    AssertEqual(JsonSerializer.Serialize(revoked), JsonSerializer.Serialize(repeated));
                    AssertEqual(revoked.AutomaticWorkPermissionWriteRevision, repeated.AutomaticWorkPermissionWriteRevision);
                    AssertEqual(4, (await AuditsAsync(db, mission.Id).ConfigureAwait(false)).Count);

                    request = Revoke(mission, MissionAutomaticActionEnum.DependentAdmission);
                    request.ExpectedStartedUtc = mission.StartedUtc!.Value.AddSeconds(1);
                    await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Revoke(mission, MissionAutomaticActionEnum.DependentAdmission);
                    request.ExpectedCommitHash = new string('b', 40);
                    await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("Grant refuses missing or wrong action binding and invalid expiry without audit", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    MissionAutomaticWorkPermissionRequest request = Grant(mission, MissionAutomaticActionEnum.Verification);
                    await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Enable()).ConfigureAwait(false);
                    List<MissionAutomaticWorkPermissionRequest> wrongBindings = new List<MissionAutomaticWorkPermissionRequest>();
                    request = Grant(mission, MissionAutomaticActionEnum.Verification); request.Action = null; wrongBindings.Add(request);
                    request = Grant(mission, MissionAutomaticActionEnum.Verification); request.Action = (MissionAutomaticActionEnum)999; wrongBindings.Add(request);
                    request = Grant(mission, MissionAutomaticActionEnum.Verification); request.ExpectedStartedUtc = null; wrongBindings.Add(request);
                    request = Grant(mission, MissionAutomaticActionEnum.Verification); request.ExpectedStartedUtc = mission.StartedUtc!.Value.AddSeconds(1); wrongBindings.Add(request);
                    request = Grant(mission, MissionAutomaticActionEnum.Verification); request.ExpectedCommitHash = null; wrongBindings.Add(request);
                    request = Grant(mission, MissionAutomaticActionEnum.Verification); request.ExpectedCommitHash = new string('b', 40); wrongBindings.Add(request);
                    foreach (MissionAutomaticWorkPermissionRequest invalid in wrongBindings)
                        await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), mission.Id, invalid).ConfigureAwait(false);
                    foreach (DateTime? expiry in new DateTime?[]
                    {
                        null, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(2),
                        DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(10), DateTimeKind.Unspecified)
                    })
                    {
                        request = Grant(mission, MissionAutomaticActionEnum.Verification);
                        request.ExpiresUtc = expiry;
                        await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    }
                    Mission running = await CreateMissionAsync(db, MissionStatusEnum.InProgress).ConfigureAwait(false);
                    await ApplyAsync(db, service, running.Id, Enable()).ConfigureAwait(false);
                    await AssertRejectedAsync<InvalidOperationException>(db, service, Operator(), running.Id,
                        Grant(running, MissionAutomaticActionEnum.Verification)).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("Enable and Revoke reject contradictory fields without changing the current grants", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    MissionAutomaticWorkPermissionRequest request = Enable();
                    request.Action = MissionAutomaticActionEnum.Verification;
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.ExpectedCommitHash = mission.CommitHash;
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.ExpectedStartedUtc = mission.StartedUtc;
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.ExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.Operation = null;
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.Operation = (MissionAutomaticPermissionOperationEnum)999;
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.Reason = " ";
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    request = Enable(); request.Reason = new string('r', 2049);
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Enable()).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Grant(mission, MissionAutomaticActionEnum.Verification)).ConfigureAwait(false);
                    request = Revoke(mission, MissionAutomaticActionEnum.Verification);
                    request.ExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                    await AssertRejectedAsync<ArgumentException>(db, service, Operator(), mission.Id, request).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("Actual event insertion failure rolls back the public service permission change", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionService service = CreateService(db);
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    await ApplyAsync(db, service, mission.Id, Enable()).ConfigureAwait(false);
                    Mission before = await ReadAsync(db, mission.Id).ConfigureAwait(false);
                    using (SqliteConnection connection = new SqliteConnection(db.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "CREATE TRIGGER reject_permission_audit BEFORE INSERT ON events "
                                + "WHEN NEW.event_type='mission.automatic_work_permission_changed' "
                                + "BEGIN SELECT RAISE(ABORT, 'permission audit storage unavailable'); END;";
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    await AssertRejectedAsync<SqliteException>(db, service, Operator(), mission.Id,
                        Grant(before, MissionAutomaticActionEnum.Verification)).ConfigureAwait(false);
                    Mission after = await ReadAsync(db, mission.Id).ConfigureAwait(false);
                    AssertEqual(before.AutomaticWorkPermissionWriteRevision, after.AutomaticWorkPermissionWriteRevision);
                    AssertEqual(before.AutomaticWorkPermissionRawJson, after.AutomaticWorkPermissionRawJson);
                    AssertNull(after.AutomaticWorkPermission!.Verification);
                    AssertEqual(1, (await AuditsAsync(db, mission.Id).ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);
        }

        private async Task AssertRejectedAsync<TException>(TestDatabase db, MissionService service, AuthContext caller,
            string missionId, MissionAutomaticWorkPermissionRequest request, string? expectedReason = null) where TException : Exception
        {
            Mission before = await ReadAsync(db, missionId).ConfigureAwait(false);
            int count = (await AuditsAsync(db, missionId).ConfigureAwait(false)).Count;
            await AssertThrowsAsync<TException>(async () =>
            {
                try
                {
                    await service.UpdateAutomaticWorkPermissionAsync(caller, missionId, request).ConfigureAwait(false);
                }
                catch (TException exception)
                {
                    if (expectedReason != null) AssertTrue(exception.Message.StartsWith(expectedReason, StringComparison.Ordinal));
                    throw;
                }
            }).ConfigureAwait(false);
            Mission after = await ReadAsync(db, missionId).ConfigureAwait(false);
            AssertEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after), "A refused operation changes no public mission state.");
            AssertEqual(before.AutomaticWorkPermissionRawJson, after.AutomaticWorkPermissionRawJson);
            AssertEqual(before.AutomaticWorkPermissionWriteRevision, after.AutomaticWorkPermissionWriteRevision);
            AssertEqual(count, (await AuditsAsync(db, missionId).ConfigureAwait(false)).Count, "A refused operation writes no permission audit.");
        }

        private async Task<Mission> ApplyAsync(TestDatabase db, MissionService service, string missionId,
            MissionAutomaticWorkPermissionRequest request)
        {
            Mission before = await ReadAsync(db, missionId).ConfigureAwait(false);
            Mission? result = await service.UpdateAutomaticWorkPermissionAsync(Operator(), missionId, request).ConfigureAwait(false);
            AssertNotNull(result);
            Mission stored = await ReadAsync(db, missionId).ConfigureAwait(false);
            AssertEqual(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(result), "The service returns the current persisted permission.");
            AssertEqual(State(before), State(stored), "Permission operations do not accept review, restart, change source or reset counters.");
            return result!;
        }

        private static MissionService CreateService(TestDatabase db)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaSettings settings = new ArmadaSettings();
            string root = Path.Combine(Path.GetTempPath(), "armada_permission_operator_" + Guid.NewGuid().ToString("N"));
            settings.DataDirectory = root;
            settings.LogDirectory = Path.Combine(root, "logs");
            settings.DocksDirectory = Path.Combine(root, "docks");
            settings.ReposDirectory = Path.Combine(root, "repos");
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, db.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, db.Driver, settings, git, docks);
            return new MissionService(logging, db.Driver, settings, docks, captains, git: git,
                resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
        }

        private static async Task<Mission> CreateMissionAsync(TestDatabase db, MissionStatusEnum status = MissionStatusEnum.Review)
        {
            Mission created = await db.Driver.Missions.CreateAsync(new Mission("Operator permission", "Keep existing duties and evidence")
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Persona = "Worker",
                Status = status, StartedUtc = DateTime.UtcNow.AddMinutes(-5), CommitHash = new string('a', 40),
                BranchName = "permission-work", RequiresReview = true, ReviewDenyAction = ReviewDenyActionEnum.RetryStage,
                ReviewRequestedUtc = DateTime.UtcNow.AddMinutes(-1), ReviewComment = "Existing review evidence",
                RecoveryAttempts = 2, LandingRetryCount = 1, RetrySkipCaptainIds = "earlier-captain",
                PreservedRunOutput = "Earlier failed attempt", AgentOutput = "Current produced report", DiffSnapshot = "Retained source diff"
            }).ConfigureAwait(false);
            return await ReadAsync(db, created.Id).ConfigureAwait(false);
        }

        private static async Task<Mission> ReadAsync(TestDatabase db, string missionId) =>
            await db.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The fixture mission must be persisted before an operator request.");

        private static async Task<List<ArmadaEvent>> AuditsAsync(TestDatabase db, string missionId) =>
            (await db.Driver.Events.EnumerateByMissionAsync(missionId, 100).ConfigureAwait(false))
                .Where(item => item.EventType == _EventType).ToList();

        private static AuthContext Operator() =>
            AuthContext.Authenticated(Constants.DefaultTenantId, _OperatorId, true, true, "Test");

        private static MissionAutomaticWorkPermissionRequest Enable() => new MissionAutomaticWorkPermissionRequest
        {
            Operation = MissionAutomaticPermissionOperationEnum.Enable, Reason = "Require separate operator admission"
        };

        private static MissionAutomaticWorkPermissionRequest Grant(Mission mission, MissionAutomaticActionEnum action) =>
            new MissionAutomaticWorkPermissionRequest
            {
                Operation = MissionAutomaticPermissionOperationEnum.Grant, Action = action,
                ExpectedStartedUtc = mission.StartedUtc, ExpectedCommitHash = mission.CommitHash,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(10), Reason = "Permit only the selected current action"
            };

        private static MissionAutomaticWorkPermissionRequest Revoke(Mission mission, MissionAutomaticActionEnum action) =>
            new MissionAutomaticWorkPermissionRequest
            {
                Operation = MissionAutomaticPermissionOperationEnum.Revoke, Action = action,
                ExpectedStartedUtc = mission.StartedUtc, ExpectedCommitHash = mission.CommitHash,
                Reason = "Withdraw only the selected current permission"
            };

        private static string State(Mission mission) => JsonSerializer.Serialize(new
        {
            mission.Id, mission.TenantId, mission.UserId, mission.VesselId, mission.VoyageId, mission.Persona, mission.Mode,
            mission.Status, mission.AssignmentState, mission.BranchName, mission.DockId, mission.ProcessId, mission.CommitHash,
            mission.StartedUtc, mission.CompletedUtc, mission.RequiresReview, mission.ReviewDenyAction, mission.ReviewedUtc,
            mission.ReviewRequestedUtc, mission.ReviewComment, mission.ReviewedByUserId, mission.HeldForOperatorReview,
            mission.HeldForOperatorReviewReason, mission.RecoveryAttempts, mission.LandingRetryCount, mission.RetrySkipCaptainIds,
            mission.ResearchReplayId, mission.PreservedRunOutput, mission.AgentOutput, mission.DiffSnapshot,
            mission.DependsOnMissionId, mission.StageOrder, mission.StageChainId, mission.CreatedUtc, mission.TotalRuntimeMs,
            mission.StartFromRef, mission.LastRecoveryActionUtc, mission.ProcessStartedUtc, mission.RequestedCaptainId, mission.PreferredModel
        });

        private static PermissionAudit ReadAudit(ArmadaEvent audit) =>
            JsonSerializer.Deserialize<PermissionAudit>(audit.Payload!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The permission change must retain its typed audit payload.");

        private sealed class PermissionAudit
        {
            /// <summary>Construct the typed audit read projection.</summary>
            public PermissionAudit() { }

            /// <summary>Recorded operator operation.</summary>
            public MissionAutomaticPermissionOperationEnum? Operation { get; set; }

            /// <summary>Selected independent permission.</summary>
            public MissionAutomaticActionEnum? Action { get; set; }

            /// <summary>Authenticated issuer, supplied by the service.</summary>
            public string? ActorUserId { get; set; }

            /// <summary>Operator reason retained with the change.</summary>
            public string? Reason { get; set; }

            /// <summary>Expected current launch.</summary>
            public DateTime? ExpectedStartedUtc { get; set; }

            /// <summary>Expected current source.</summary>
            public string? ExpectedCommitHash { get; set; }

            /// <summary>Requested expiry for a grant.</summary>
            public DateTime? ExpiresUtc { get; set; }

            /// <summary>Revision compared by the conditional write.</summary>
            public long ExpectedRevision { get; set; }

            /// <summary>Revision committed with this audit.</summary>
            public long NewRevision { get; set; }
        }
    }
}
