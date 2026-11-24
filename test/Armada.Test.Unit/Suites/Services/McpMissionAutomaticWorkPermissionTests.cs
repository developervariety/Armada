namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>Registered permission-handler authorization and persistence through the real mission service.</summary>
    public sealed class McpMissionAutomaticWorkPermissionTests : TestSuite
    {
        private const string _Tool = "armada_mission_action_permissions";
        private const string _Event = "mission.automatic_work_permission_changed";
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <inheritdoc />
        public override string Name => "MCP Mission Automatic Work Permission";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Captain and full or partial Ask scope cannot use the catalog or registered handler", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    AuthContext captain = Administrator(); captain.MissionId = mission.Id;
                    AuthContext thread = Administrator(); thread.AskThreadId = "saved-thread";
                    AuthContext turn = Administrator(); turn.AskTurnId = "saved-turn";
                    AuthContext ask = Administrator(); ask.AskThreadId = "saved-thread"; ask.AskTurnId = "saved-turn";
                    foreach (AuthContext caller in new[] { captain, thread, turn, ask })
                    {
                        AssertFalse(McpToolAccessPolicy.IsAllowed(caller, _Tool));
                        foreach (string operation in new[] { "status", "Enable" })
                            await AssertRefusedWithoutWriteAsync(fixture, caller, mission, Arguments(mission, operation), "operator_required").ConfigureAwait(false);
                    }
                    AssertTrue(McpToolAccessPolicy.IsAllowed(Administrator(), _Tool));
                }
            }).ConfigureAwait(false);

            await RunTest("Missing or unauthenticated async caller is rejected before any permission or audit write", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    AssertNull(McpCallerContext.Current);
                    await AssertThrowsAsync<UnauthorizedAccessException>(async () =>
                    {
                        await fixture.Handler(Arguments(mission, "Enable")).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    AuthContext caller = Administrator();
                    using (McpCallerContext.Begin(caller))
                    {
                        caller.IsAuthenticated = false;
                        AssertFalse(McpToolAccessPolicy.IsAllowed(caller, _Tool));
                        await AssertThrowsAsync<UnauthorizedAccessException>(async () =>
                        {
                            await fixture.Handler(Arguments(mission, "Enable")).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    }
                    await AssertUnchangedAsync(fixture, mission, 0).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("A low-role owner can read handler status but every write is refused without side effects", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Test");
                    AssertFalse(McpToolAccessPolicy.IsAllowed(caller, _Tool), "The combined tool remains operator-only in the catalog.");
                    Reply status = ReadReply(await InvokeAsync(fixture, caller, Arguments(mission, "status")).ConfigureAwait(false));
                    AssertEqual(mission.Id, status.Id);
                    AssertNull(status.AutomaticWorkPermission);
                    foreach (string operation in new[] { "Enable", "Grant", "Revoke" })
                        await AssertRefusedWithoutWriteAsync(fixture, caller, mission, Arguments(mission, operation), "permission_denied").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("Status follows owner tenant administrator and global administrator scope without exposing reports", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission own = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    UserMaster peerUser = await fixture.Database.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "permission-peer@example.invalid", "fixture-password")).ConfigureAwait(false);
                    TenantMetadata foreignTenant = await fixture.Database.Driver.Tenants.CreateAsync(new TenantMetadata("permission foreign tenant")).ConfigureAwait(false);
                    UserMaster foreignUser = await fixture.Database.Driver.Users.CreateAsync(new UserMaster(foreignTenant.Id, "permission-foreign@example.invalid", "fixture-password")).ConfigureAwait(false);
                    Mission peer = await CreateMissionAsync(fixture.Database, Constants.DefaultTenantId, peerUser.Id).ConfigureAwait(false);
                    Mission foreign = await CreateMissionAsync(fixture.Database, foreignTenant.Id, foreignUser.Id).ConfigureAwait(false);
                    AuthContext owner = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Test");
                    AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, "permission-tenant-admin", false, true, "Test");
                    foreach (AuthContext caller in new[] { owner, tenantAdmin, Administrator() })
                    {
                        object result = await InvokeAsync(fixture, caller, Arguments(own, "StAtUs")).ConfigureAwait(false);
                        Reply reply = ReadReply(result);
                        AssertEqual(own.Id, reply.Id);
                        AssertEqual(own.StartedUtc, reply.StartedUtc);
                        AssertEqual(own.CommitHash, reply.CommitHash);
                        AssertFalse(reply.AutomaticWorkPermissionInvalid);
                        string json = JsonSerializer.Serialize(result);
                        AssertFalse(json.Contains(own.Description!, StringComparison.Ordinal));
                        AssertFalse(json.Contains(own.AgentOutput!, StringComparison.Ordinal));
                    }
                    await AssertRefusedWithoutWriteAsync(fixture, owner, peer, Arguments(peer, "status"), "mission_not_found").ConfigureAwait(false);
                    await AssertRefusedWithoutWriteAsync(fixture, owner, foreign, Arguments(foreign, "status"), "mission_not_found").ConfigureAwait(false);
                    await AssertRefusedWithoutWriteAsync(fixture, tenantAdmin, foreign, Arguments(foreign, "status"), "mission_not_found").ConfigureAwait(false);
                    AssertEqual(peer.Id, ReadReply(await InvokeAsync(fixture, tenantAdmin, Arguments(peer, "status")).ConfigureAwait(false)).Id);
                    AssertEqual(foreign.Id, ReadReply(await InvokeAsync(fixture, Administrator(), Arguments(foreign, "status")).ConfigureAwait(false)).Id);
                    AssertEqual(0, (await AuditsAsync(fixture.Database, own.Id).ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);

            await RunTest("A foreign write and an absent mission return the same safe not-found result", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    AuthContext foreign = AuthContext.Authenticated("foreign-tenant", "foreign-operator", false, true, "Test");
                    object hidden = await InvokeAsync(fixture, foreign, Arguments(mission, "Enable")).ConfigureAwait(false);
                    object missing = await InvokeAsync(fixture, Administrator(), JsonSerializer.SerializeToElement(new { missionId = "msn_absent", operation = "Enable", reason = "Require permission" })).ConfigureAwait(false);
                    AssertEqual("mission_not_found", ReadReply(hidden).Reason);
                    AssertEqual(JsonSerializer.Serialize(missing), JsonSerializer.Serialize(hidden));
                    await AssertUnchangedAsync(fixture, mission, 0).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("Malformed body dates operations and actions return safe argument errors with no write", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    string privateInput = "private-invalid-input";
                    List<JsonElement?> inputs = new List<JsonElement?>
                    {
                        null, JsonSerializer.SerializeToElement<object?>(null), JsonSerializer.SerializeToElement(new[] { privateInput }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = privateInput, reason = "Require permission" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "999", reason = "Require permission" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Grant", action = privateInput, reason = "Require permission" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Grant", action = "999", reason = "Require permission" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Grant", action = "Verification", expectedStartedUtc = privateInput, reason = "Require permission" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Grant", action = "Verification", expiresUtc = privateInput, reason = "Require permission" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = 1, reason = "Require permission" })
                    };
                    foreach (JsonElement? input in inputs)
                    {
                        object result = await InvokeAsync(fixture, Administrator(), input).ConfigureAwait(false);
                        Reply reply = ReadReply(result);
                        AssertEqual("invalid_arguments", reply.Reason);
                        AssertFalse(String.IsNullOrWhiteSpace(reply.Error));
                        AssertFalse(JsonSerializer.Serialize(result).Contains(privateInput, StringComparison.Ordinal), "Malformed payload values must not leak through JsonException diagnostics.");
                        await AssertUnchangedAsync(fixture, mission, 0).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Enable Grant and Revoke return fresh SQLite state and record scoped authenticated issuer audit", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    AuthContext caller = Administrator();
                    DateTime expiry = DateTime.UtcNow.AddMinutes(10);
                    Mission enabled = await ApplyAsync(fixture, caller, Arguments(mission, "eNaBlE")).ConfigureAwait(false);
                    AssertNotNull(enabled.AutomaticWorkPermission);
                    AssertNull(enabled.AutomaticWorkPermission!.Verification);
                    AssertNull(enabled.AutomaticWorkPermission.DependentAdmission);
                    Mission verification = await ApplyAsync(fixture, caller, Arguments(mission, "gRaNt", "Verification", expiry)).ConfigureAwait(false);
                    AssertGrant(verification.AutomaticWorkPermission!.Verification!, mission, caller.UserId!, expiry);
                    AssertNull(verification.AutomaticWorkPermission.DependentAdmission);
                    Mission both = await ApplyAsync(fixture, caller, Arguments(mission, "Grant", "DependentAdmission", expiry)).ConfigureAwait(false);
                    AssertGrant(both.AutomaticWorkPermission!.DependentAdmission!, mission, caller.UserId!, expiry);
                    AssertEqual(JsonSerializer.Serialize(verification.AutomaticWorkPermission.Verification), JsonSerializer.Serialize(both.AutomaticWorkPermission.Verification));
                    Mission revoked = await ApplyAsync(fixture, caller, Arguments(mission, "Revoke", "Verification")).ConfigureAwait(false);
                    AssertNull(revoked.AutomaticWorkPermission!.Verification);
                    AssertEqual(JsonSerializer.Serialize(both.AutomaticWorkPermission.DependentAdmission), JsonSerializer.Serialize(revoked.AutomaticWorkPermission.DependentAdmission));
                    Mission empty = await ApplyAsync(fixture, caller, Arguments(mission, "Revoke", "DependentAdmission")).ConfigureAwait(false);
                    AssertNull(empty.AutomaticWorkPermission!.Verification);
                    AssertNull(empty.AutomaticWorkPermission.DependentAdmission);
                    AssertEqual(5L, empty.AutomaticWorkPermissionWriteRevision);
                    List<ArmadaEvent> audits = (await AuditsAsync(fixture.Database, mission.Id).ConfigureAwait(false)).OrderBy(item => ReadAudit(item).NewRevision).ToList();
                    AssertEqual(5, audits.Count);
                    MissionAutomaticPermissionOperationEnum[] operations = { MissionAutomaticPermissionOperationEnum.Enable, MissionAutomaticPermissionOperationEnum.Grant, MissionAutomaticPermissionOperationEnum.Grant, MissionAutomaticPermissionOperationEnum.Revoke, MissionAutomaticPermissionOperationEnum.Revoke };
                    MissionAutomaticActionEnum?[] actions = { null, MissionAutomaticActionEnum.Verification, MissionAutomaticActionEnum.DependentAdmission, MissionAutomaticActionEnum.Verification, MissionAutomaticActionEnum.DependentAdmission };
                    for (int index = 0; index < audits.Count; index++)
                    {
                        ArmadaEvent audit = audits[index]; PermissionAudit payload = ReadAudit(audit);
                        AssertEqual(mission.Id, audit.MissionId); AssertEqual(mission.Id, audit.EntityId);
                        AssertEqual(mission.TenantId, audit.TenantId); AssertEqual(mission.UserId, audit.UserId);
                        AssertEqual(caller.UserId, payload.ActorUserId);
                        AssertEqual("Explicit operator action", payload.Reason);
                        AssertEqual(operations[index], payload.Operation);
                        AssertEqual(actions[index], payload.Action);
                        AssertEqual((long)index, payload.ExpectedRevision); AssertEqual((long)index + 1, payload.NewRevision);
                        AssertEqual(index == 0 ? null : mission.StartedUtc, payload.ExpectedStartedUtc);
                        AssertEqual(index == 0 ? null : mission.CommitHash, payload.ExpectedCommitHash);
                        AssertEqual(index == 1 || index == 2 ? expiry : (DateTime?)null, payload.ExpiresUtc);
                    }
                    Reply status = ReadReply(await InvokeAsync(fixture, caller, Arguments(mission, "status")).ConfigureAwait(false));
                    AssertEqual(JsonSerializer.Serialize(empty.AutomaticWorkPermission), JsonSerializer.Serialize(status.AutomaticWorkPermission));
                    AssertEqual(mission.Status, empty.Status); AssertEqual(mission.RecoveryAttempts, empty.RecoveryAttempts);
                    AssertEqual(mission.BranchName, empty.BranchName); AssertEqual(mission.AgentOutput, empty.AgentOutput);
                    AssertEqual(mission.RequiresReview, empty.RequiresReview); AssertNull(empty.ReviewedUtc);
                }
            }).ConfigureAwait(false);

            await RunTest("Service binding conflicts and invalid request fields are safe handler errors without extra audit", async () =>
            {
                using (Fixture fixture = await Fixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(fixture.Database).ConfigureAwait(false);
                    await ApplyAsync(fixture, Administrator(), Arguments(mission, "Enable")).ConfigureAwait(false);
                    Mission enabled = await ReadAsync(fixture.Database, mission.Id).ConfigureAwait(false);
                    foreach (JsonElement invalid in new[]
                    {
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Grant", action = "Verification", expectedStartedUtc = mission.StartedUtc, expectedCommitHash = "private-stale-source", expiresUtc = DateTime.UtcNow.AddMinutes(10), reason = "Explicit operator action" }),
                        JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Grant", action = "Verification", expectedStartedUtc = mission.StartedUtc!.Value.AddSeconds(1), expectedCommitHash = mission.CommitHash, expiresUtc = DateTime.UtcNow.AddMinutes(10), reason = "Explicit operator action" })
                    })
                        await AssertRefusedWithoutWriteAsync(fixture, Administrator(), enabled, invalid, "permission_conflict", 1).ConfigureAwait(false);
                    await AssertRefusedWithoutWriteAsync(fixture, Administrator(), enabled, Arguments(mission, "Grant", "Verification", DateTime.UtcNow.AddHours(2)), "invalid_arguments", 1).ConfigureAwait(false);
                    await AssertRefusedWithoutWriteAsync(fixture, Administrator(), enabled, JsonSerializer.SerializeToElement(new { missionId = mission.Id, operation = "Enable", action = "Verification", reason = "Explicit operator action" }), "invalid_arguments", 1).ConfigureAwait(false);
                    AuthContext noUser = Administrator(); noUser.UserId = null;
                    await AssertRefusedWithoutWriteAsync(fixture, noUser, enabled, Arguments(mission, "Revoke", "Verification"), "permission_denied", 1).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        private static AuthContext Administrator() => AuthContext.Authenticated(Constants.DefaultTenantId, "permission-mcp-issuer", true, true, "Test");

        private static JsonElement Arguments(Mission mission, string operation, string? action = null, DateTime? expiry = null) =>
            JsonSerializer.SerializeToElement(new
            {
                missionId = mission.Id, operation, action,
                expectedStartedUtc = action == null ? null : mission.StartedUtc,
                expectedCommitHash = action == null ? null : mission.CommitHash,
                expiresUtc = expiry, reason = "Explicit operator action",
                actorUserId = "untrusted-body-issuer", grantedByUserId = "untrusted-body-issuer"
            });

        private static async Task<object> InvokeAsync(Fixture fixture, AuthContext caller, JsonElement? arguments)
        {
            using (McpCallerContext.Begin(caller))
                return await fixture.Handler(arguments).ConfigureAwait(false);
        }

        private async Task AssertRefusedWithoutWriteAsync(Fixture fixture, AuthContext caller, Mission before, JsonElement? arguments,
            string reason, int audits = 0)
        {
            object result = await InvokeAsync(fixture, caller, arguments).ConfigureAwait(false);
            Reply reply = ReadReply(result);
            AssertEqual(reason, reply.Reason); AssertFalse(String.IsNullOrWhiteSpace(reply.Error));
            AssertFalse(JsonSerializer.Serialize(result).Contains("private-stale-source", StringComparison.Ordinal));
            await AssertUnchangedAsync(fixture, before, audits).ConfigureAwait(false);
        }

        private async Task AssertUnchangedAsync(Fixture fixture, Mission before, int audits)
        {
            Mission after = await ReadAsync(fixture.Database, before.Id).ConfigureAwait(false);
            AssertEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
            AssertEqual(before.AutomaticWorkPermissionRawJson, after.AutomaticWorkPermissionRawJson);
            AssertEqual(before.AutomaticWorkPermissionWriteRevision, after.AutomaticWorkPermissionWriteRevision);
            AssertEqual(audits, (await AuditsAsync(fixture.Database, before.Id).ConfigureAwait(false)).Count);
        }

        private async Task<Mission> ApplyAsync(Fixture fixture, AuthContext caller, JsonElement arguments)
        {
            object result = await InvokeAsync(fixture, caller, arguments).ConfigureAwait(false);
            AssertTrue(result is Mission, "A successful mutation returns the persisted Mission, not a transport-only success marker.");
            Mission stored = await ReadAsync(fixture.Database, ((Mission)result).Id).ConfigureAwait(false);
            AssertEqual(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(result));
            return stored;
        }

        private static Reply ReadReply(object result) => JsonSerializer.Deserialize<Reply>(JsonSerializer.Serialize(result), _Json)
            ?? throw new InvalidOperationException("The handler must return a typed result or a refusal.");

        private static PermissionAudit ReadAudit(ArmadaEvent audit) => JsonSerializer.Deserialize<PermissionAudit>(audit.Payload!, _Json)
            ?? throw new InvalidOperationException("The permission audit must have a payload.");

        private void AssertGrant(MissionAutomaticWorkGrant grant, Mission mission, string issuer, DateTime expiry)
        {
            AssertNotNull(grant); AssertEqual(mission.Id, grant.MissionId);
            AssertEqual(mission.TenantId, grant.TenantId); AssertEqual(mission.UserId, grant.UserId);
            AssertEqual(mission.StartedUtc!.Value, grant.StartedUtc); AssertEqual(mission.CommitHash, grant.CommitHash);
            AssertEqual(issuer, grant.GrantedByUserId); AssertEqual(expiry, grant.ExpiresUtc);
            AssertTrue(grant.GrantedUtc >= mission.StartedUtc.Value && grant.GrantedUtc <= DateTime.UtcNow);
        }

        private static async Task<Mission> CreateMissionAsync(TestDatabase database, string? tenant = null, string? owner = null)
        {
            Mission created = await database.Driver.Missions.CreateAsync(new Mission("Permission MCP fixture", "private fixture instructions")
            {
                TenantId = tenant ?? Constants.DefaultTenantId, UserId = owner ?? Constants.DefaultUserId,
                Persona = "Worker", Status = MissionStatusEnum.Review, StartedUtc = DateTime.UtcNow.AddMinutes(-5),
                CommitHash = new string('a', 40), BranchName = "permission-fixture", RequiresReview = true,
                RecoveryAttempts = 2, PreservedRunOutput = "Earlier retained attempt", AgentOutput = "private fixture report"
            }).ConfigureAwait(false);
            return await ReadAsync(database, created.Id).ConfigureAwait(false);
        }

        private static async Task<Mission> ReadAsync(TestDatabase database, string id) =>
            await database.Driver.Missions.ReadAsync(id).ConfigureAwait(false) ?? throw new InvalidOperationException("Fixture mission missing.");

        private static async Task<List<ArmadaEvent>> AuditsAsync(TestDatabase database, string missionId) =>
            (await database.Driver.Events.EnumerateByMissionAsync(missionId, 100).ConfigureAwait(false)).Where(item => item.EventType == _Event).ToList();

        private sealed class Fixture : IDisposable
        {
            public TestDatabase Database { get; }
            public Func<JsonElement?, Task<object>> Handler { get; }

            private Fixture(TestDatabase database)
            {
                Database = database;
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                string directory = Path.Combine(Path.GetTempPath(), "armada_permission_mcp_" + Guid.NewGuid().ToString("N"));
                settings.DataDirectory = directory; settings.LogDirectory = Path.Combine(directory, "logs");
                settings.DocksDirectory = Path.Combine(directory, "docks"); settings.ReposDirectory = Path.Combine(directory, "repos");
                StubGitService git = new StubGitService();
                IDockService docks = new DockService(logging, database.Driver, settings, git);
                ICaptainService captains = new CaptainService(logging, database.Driver, settings, git, docks);
                MissionService service = new MissionService(logging, database.Driver, settings, docks, captains, git: git,
                    resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                Func<JsonElement?, Task<object>>? handler = null;
                McpMissionAutomaticWorkPermissionTools.Register((name, _, _, registered) =>
                {
                    if (name == _Tool) handler = registered;
                }, database.Driver, service);
                Handler = handler ?? throw new InvalidOperationException("The permission MCP handler was not registered.");
            }

            public static async Task<Fixture> CreateAsync() => new Fixture(await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false));
            public void Dispose() => Database.Dispose();
        }

        private sealed class Reply
        {
            public Reply() { }
            public string? Error { get; set; }
            public string? Reason { get; set; }
            public string? Id { get; set; }
            public DateTime? StartedUtc { get; set; }
            public string? CommitHash { get; set; }
            public MissionAutomaticWorkPermission? AutomaticWorkPermission { get; set; }
            public bool AutomaticWorkPermissionInvalid { get; set; }
        }

        private sealed class PermissionAudit
        {
            public PermissionAudit() { }
            public MissionAutomaticPermissionOperationEnum Operation { get; set; }
            public MissionAutomaticActionEnum? Action { get; set; }
            public string? ActorUserId { get; set; }
            public string? Reason { get; set; }
            public DateTime? ExpectedStartedUtc { get; set; }
            public string? ExpectedCommitHash { get; set; }
            public DateTime? ExpiresUtc { get; set; }
            public long ExpectedRevision { get; set; }
            public long NewRevision { get; set; }
        }
    }
}
