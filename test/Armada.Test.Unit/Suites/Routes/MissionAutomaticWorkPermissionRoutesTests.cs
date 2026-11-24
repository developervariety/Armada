namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using HttpMethod = System.Net.Http.HttpMethod;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using WatsonWebserver.Core;

    /// <summary>Real HTTP authentication, caller scope and wire contracts for mission action permissions.</summary>
    public sealed class MissionAutomaticWorkPermissionRoutesTests : TestSuite
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <inheritdoc />
        public override string Name => "Mission Automatic Work Permission Routes";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Permission GET and POST require authentication and reject a real captain session", async () =>
            {
                using (SurfaceParityHarness h = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await SeedAsync(h).ConfigureAwait(false);
                    string token = h.CreateMissionToken(mission.TenantId!, mission.UserId!, mission.Id);
                    foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post })
                    {
                        object? body = method == HttpMethod.Post ? Enable() : null;
                        SurfaceReply anonymous = await AnonymousAsync(h, method, Path(mission.Id), body).ConfigureAwait(false);
                        AssertEqual(401, anonymous.Status, anonymous.ToString());
                        AssertEqual(ApiResultEnum.NotAuthorized, Read<ApiErrorResponse>(anonymous).Error);
                        SurfaceReply captain = await h.RestAsync(method, Path(mission.Id), body, token).ConfigureAwait(false);
                        AssertEqual(403, captain.Status, captain.ToString());
                        AssertEqual(ApiResultEnum.Forbidden, Read<ApiErrorResponse>(captain).Error);
                        if (method == HttpMethod.Get)
                            AssertContains("operator control", Read<ApiErrorResponse>(captain).Message!);
                    }
                    AssertEqual(JsonSerializer.Serialize(mission), JsonSerializer.Serialize(await ReadMissionAsync(h, mission.Id).ConfigureAwait(false)));
                    AssertEqual(0, (await AuditsAsync(h, mission.Id).ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);

            await RunTest("HTTP preserves low-role read scope and write refusal while tenant administrators write only within their tenant", async () =>
            {
                using (SurfaceParityHarness h = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    UserMaster owner = await h.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId,
                        "permission-route-owner@example.test", "route-password-923") { IsAdmin = false, IsTenantAdmin = false }).ConfigureAwait(false);
                    string token = await LoginAsync(h, owner, "route-password-923").ConfigureAwait(false);
                    Mission own = await SeedAsync(h, owner.TenantId, owner.Id).ConfigureAwait(false);
                    Mission otherOwner = await SeedAsync(h).ConfigureAwait(false);
                    TenantMetadata tenant = await h.Driver.Tenants.CreateAsync(new TenantMetadata("permission-route-foreign")).ConfigureAwait(false);
                    UserMaster foreign = await h.Driver.Users.CreateAsync(new UserMaster(tenant.Id,
                        "permission-route-foreign@example.test", "route-password-923")).ConfigureAwait(false);
                    Mission otherTenant = await SeedAsync(h, tenant.Id, foreign.Id).ConfigureAwait(false);
                    SurfaceReply ownRead = await h.RestAsync(HttpMethod.Get, Path(own.Id), null, token).ConfigureAwait(false);
                    AssertEqual(200, ownRead.Status, ownRead.ToString());
                    AssertEqual(own.Id, Read<PermissionReadback>(ownRead).Id);
                    foreach (Mission hidden in new[] { otherOwner, otherTenant })
                    {
                        SurfaceReply reply = await h.RestAsync(HttpMethod.Get, Path(hidden.Id), null, token).ConfigureAwait(false);
                        AssertEqual(404, reply.Status, reply.ToString());
                        AssertEqual(ApiResultEnum.NotFound, Read<ApiErrorResponse>(reply).Error);
                        AssertEqual("Mission not found", Read<ApiErrorResponse>(reply).Message);
                        AssertFalse(reply.Body.Contains(hidden.CommitHash!, StringComparison.Ordinal));
                    }
                    foreach (Mission target in new[] { own, otherOwner, otherTenant })
                    {
                        // The role matrix refuses every mission POST before the caller-scoped read.
                        SurfaceReply refused = await h.RestAsync(HttpMethod.Post, Path(target.Id), Enable(), token).ConfigureAwait(false);
                        AssertEqual(403, refused.Status, refused.ToString());
                        AssertEqual(ApiResultEnum.Forbidden, Read<ApiErrorResponse>(refused).Error);
                        AssertEqual(JsonSerializer.Serialize(target), JsonSerializer.Serialize(await ReadMissionAsync(h, target.Id).ConfigureAwait(false)));
                        AssertEqual(0, (await AuditsAsync(h, target.Id).ConfigureAwait(false)).Count);
                    }

                    UserMaster tenantAdmin = await h.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId,
                        "permission-route-admin@example.test", "route-password-923") { IsAdmin = false, IsTenantAdmin = true }).ConfigureAwait(false);
                    string adminToken = await LoginAsync(h, tenantAdmin, "route-password-923").ConfigureAwait(false);
                    // A tenant administrator may act on another owner in its own tenant.
                    SurfaceReply allowed = await h.RestAsync(HttpMethod.Post, Path(otherOwner.Id), Enable(), adminToken).ConfigureAwait(false);
                    AssertEqual(200, allowed.Status, allowed.ToString());
                    PermissionWriteback written = Read<PermissionWriteback>(allowed);
                    AssertEqual(otherOwner.Id, written.Id);
                    AssertNotNull(written.AutomaticWorkPermission);
                    SurfaceReply read = await h.RestAsync(HttpMethod.Get, Path(otherOwner.Id), null, adminToken).ConfigureAwait(false);
                    AssertEqual(200, read.Status, read.ToString());
                    AssertEqual(otherOwner.Id, Read<PermissionReadback>(read).Id);
                    ArmadaEvent audit = (await AuditsAsync(h, otherOwner.Id).ConfigureAwait(false)).Single();
                    AssertEqual(tenantAdmin.Id, ReadAudit(audit).ActorUserId);
                    AssertEqual(otherOwner.UserId, audit.UserId);
                    AssertEqual(otherOwner.TenantId, audit.TenantId);
                    foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post })
                    {
                        SurfaceReply hidden = await h.RestAsync(method, Path(otherTenant.Id), method == HttpMethod.Post ? Enable() : null, adminToken).ConfigureAwait(false);
                        AssertEqual(404, hidden.Status, hidden.ToString());
                        AssertEqual(ApiResultEnum.NotFound, Read<ApiErrorResponse>(hidden).Error);
                        AssertEqual("Mission not found", Read<ApiErrorResponse>(hidden).Message);
                        AssertFalse(hidden.Body.Contains(otherTenant.CommitHash!, StringComparison.Ordinal));
                    }
                    AssertEqual(JsonSerializer.Serialize(otherTenant), JsonSerializer.Serialize(await ReadMissionAsync(h, otherTenant.Id).ConfigureAwait(false)));
                    AssertEqual(0, (await AuditsAsync(h, otherTenant.Id).ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);

            await RunTest("HTTP string enums round trip enable grant read and revoke with the durable operator audit", async () =>
            {
                using (SurfaceParityHarness h = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await SeedAsync(h).ConfigureAwait(false);
                    mission.StartedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(7);
                    await h.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    SurfaceReply enabled = await h.RestAsync(HttpMethod.Post, Path(mission.Id), Enable()).ConfigureAwait(false);
                    AssertEqual(200, enabled.Status, enabled.ToString());
                    SurfaceReply initialRead = await h.RestAsync(HttpMethod.Get, Path(mission.Id)).ConfigureAwait(false);
                    AssertEqual(200, initialRead.Status, initialRead.ToString());
                    PermissionReadback current = Read<PermissionReadback>(initialRead);
                    AssertEqual(mission.StartedUtc.Value.Ticks, current.StartedUtc!.Value.Ticks,
                        "GET must preserve the exact launch for the next operator request: " + initialRead.Body);
                    DateTime expires = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
                        DateTimeKind.Utc).AddMinutes(10).AddTicks(1234567);
                    SurfaceReply granted = await h.RestAsync(HttpMethod.Post, Path(mission.Id), new
                    {
                        operation = "Grant", action = "Verification", expectedStartedUtc = current.StartedUtc,
                        expectedCommitHash = current.CommitHash, expiresUtc = expires, reason = "Admit this HTTP verification"
                    }).ConfigureAwait(false);
                    AssertEqual(200, granted.Status, granted.ToString());
                    PermissionWriteback response = Read<PermissionWriteback>(granted);
                    Mission persisted = await ReadMissionAsync(h, mission.Id).ConfigureAwait(false);
                    AssertEqual(persisted.Id, response.Id);
                    AssertEqual(persisted.StartedUtc?.Ticks, response.StartedUtc?.Ticks,
                        "POST launch ticks must remain exact: " + granted.Body);
                    AssertEqual(persisted.CommitHash, response.CommitHash);
                    AssertEqual(JsonSerializer.Serialize(persisted.AutomaticWorkPermission), JsonSerializer.Serialize(response.AutomaticWorkPermission));
                    AssertEqual(MissionStatusEnum.Review, response.Status);
                    AssertNull(response.ReviewedUtc);

                    SurfaceReply read = await h.RestAsync(HttpMethod.Get, Path(mission.Id)).ConfigureAwait(false);
                    AssertEqual(200, read.Status, read.ToString());
                    PermissionReadback projection = Read<PermissionReadback>(read);
                    AssertEqual(mission.Id, projection.Id);
                    AssertEqual(mission.StartedUtc, projection.StartedUtc);
                    AssertEqual(mission.CommitHash, projection.CommitHash);
                    AssertFalse(projection.AutomaticWorkPermissionInvalid);
                    AssertEqual(JsonSerializer.Serialize(response.AutomaticWorkPermission), JsonSerializer.Serialize(projection.AutomaticWorkPermission));
                    AssertNull(projection.AutomaticWorkPermission!.DependentAdmission);
                    AssertEqual(expires.Ticks, projection.AutomaticWorkPermission.Verification!.ExpiresUtc.Ticks,
                        "GET expiry ticks must remain exact: " + read.Body);
                    AssertEqual(h.Caller.UserId, projection.AutomaticWorkPermission.Verification.GrantedByUserId);
                    List<ArmadaEvent> audits = await AuditsAsync(h, mission.Id).ConfigureAwait(false);
                    AssertEqual(2, audits.Count);
                    PermissionAudit grantAudit = ReadAudit(audits.Single(item => ReadAudit(item).Operation == MissionAutomaticPermissionOperationEnum.Grant));
                    AssertEqual(h.Caller.UserId, grantAudit.ActorUserId);
                    AssertEqual(MissionAutomaticActionEnum.Verification, grantAudit.Action!.Value);

                    SurfaceReply revoked = await h.RestAsync(HttpMethod.Post, Path(mission.Id), new
                    {
                        operation = "Revoke", action = "Verification", expectedStartedUtc = mission.StartedUtc,
                        expectedCommitHash = mission.CommitHash, reason = "Withdraw this HTTP verification"
                    }).ConfigureAwait(false);
                    AssertEqual(200, revoked.Status, revoked.ToString());
                    AssertNull(Read<PermissionWriteback>(revoked).AutomaticWorkPermission!.Verification);
                    AssertEqual(3, (await AuditsAsync(h, mission.Id).ConfigureAwait(false)).Count);
                    AssertEqual(MissionStatusEnum.Review, (await ReadMissionAsync(h, mission.Id).ConfigureAwait(false)).Status);
                    AssertEqual(0, h.Git.WorktreeCalls.Count);
                    AssertEqual(0, h.Admiral.Recalled.Count);
                }
            }).ConfigureAwait(false);

            await RunTest("REST maps invalid JSON and arguments to 400 stale binding to 409 and missing IDs to 404 without writes", async () =>
            {
                using (SurfaceParityHarness h = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await SeedAsync(h).ConfigureAwait(false);
                    AssertEqual(200, (await h.RestAsync(HttpMethod.Post, Path(mission.Id), Enable()).ConfigureAwait(false)).Status);
                    Mission before = await ReadMissionAsync(h, mission.Id).ConfigureAwait(false);
                    using (StringContent invalid = new StringContent("{\"operation\":", Encoding.UTF8, "application/json"))
                    using (HttpResponseMessage raw = await h.Rest.PostAsync(Path(mission.Id), invalid).ConfigureAwait(false))
                    {
                        AssertEqual(400, (int)raw.StatusCode);
                        ApiErrorResponse error = JsonSerializer.Deserialize<ApiErrorResponse>(await raw.Content.ReadAsStringAsync().ConfigureAwait(false), _Json)!;
                        AssertEqual(ApiResultEnum.BadRequest, error.Error);
                        // Typed HTTP body parsing may reject the JSON before the route handler runs.
                        AssertFalse(String.IsNullOrWhiteSpace(error.Message));
                    }
                    SurfaceReply invalidArgument = await h.RestAsync(HttpMethod.Post, Path(mission.Id), new
                    {
                        operation = "Enable", action = "Verification", reason = "Contradictory HTTP fields"
                    }).ConfigureAwait(false);
                    AssertEqual(400, invalidArgument.Status, invalidArgument.ToString());
                    AssertEqual(ApiResultEnum.BadRequest, Read<ApiErrorResponse>(invalidArgument).Error);
                    SurfaceReply stale = await h.RestAsync(HttpMethod.Post, Path(mission.Id), new
                    {
                        operation = "Grant", action = "Verification", expectedStartedUtc = mission.StartedUtc,
                        expectedCommitHash = new string('b', 40), expiresUtc = DateTime.UtcNow.AddMinutes(10), reason = "Stale HTTP source"
                    }).ConfigureAwait(false);
                    AssertEqual(409, stale.Status, stale.ToString());
                    AssertEqual(ApiResultEnum.Conflict, Read<ApiErrorResponse>(stale).Error);
                    AssertContains("automatic_work_permission_binding_changed", Read<ApiErrorResponse>(stale).Message!);
                    foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post })
                    {
                        SurfaceReply missing = await h.RestAsync(method, Path("msn_permission_route_missing"), method == HttpMethod.Post ? Enable() : null).ConfigureAwait(false);
                        AssertEqual(404, missing.Status, missing.ToString());
                        AssertEqual(ApiResultEnum.NotFound, Read<ApiErrorResponse>(missing).Error);
                    }
                    Mission after = await ReadMissionAsync(h, mission.Id).ConfigureAwait(false);
                    AssertEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
                    AssertEqual(before.AutomaticWorkPermissionWriteRevision, after.AutomaticWorkPermissionWriteRevision);
                    AssertEqual(1, (await AuditsAsync(h, mission.Id).ConfigureAwait(false)).Count);
                }
            }).ConfigureAwait(false);
        }

        private static string Path(string id) => "/api/v1/missions/" + id + "/action-permissions";

        private static object Enable() => new { operation = "Enable", reason = "Require explicit HTTP operator admission" };

        private static T Read<T>(SurfaceReply reply) where T : class => JsonSerializer.Deserialize<T>(reply.Body, _Json)
            ?? throw new InvalidOperationException("The HTTP response must match its declared type: " + reply);

        private static PermissionAudit ReadAudit(ArmadaEvent audit) => JsonSerializer.Deserialize<PermissionAudit>(audit.Payload!, _Json)
            ?? throw new InvalidOperationException("The HTTP write must retain its typed audit.");

        private static async Task<Mission> SeedAsync(SurfaceParityHarness h, string? tenant = null, string? user = null)
        {
            Mission created = await h.Driver.Missions.CreateAsync(new Mission("HTTP permission mission", "Preserve the manual review boundary")
            {
                TenantId = tenant ?? Constants.DefaultTenantId, UserId = user ?? Constants.DefaultUserId,
                Status = MissionStatusEnum.Review, Persona = "Worker", RequiresReview = true,
                StartedUtc = DateTime.UtcNow.AddMinutes(-5), CommitHash = new string('a', 40)
            }).ConfigureAwait(false);
            return await ReadMissionAsync(h, created.Id).ConfigureAwait(false);
        }

        private static async Task<Mission> ReadMissionAsync(SurfaceParityHarness h, string id) =>
            await h.Driver.Missions.ReadAsync(id).ConfigureAwait(false) ?? throw new InvalidOperationException("Missing HTTP fixture mission.");

        private static async Task<List<ArmadaEvent>> AuditsAsync(SurfaceParityHarness h, string id) =>
            (await h.Driver.Events.EnumerateByMissionAsync(id, 100).ConfigureAwait(false))
                .Where(item => item.EventType == "mission.automatic_work_permission_changed").ToList();

        private async Task<string> LoginAsync(SurfaceParityHarness h, UserMaster owner, string password)
        {
            SurfaceReply reply = await AnonymousAsync(h, HttpMethod.Post, "/api/v1/authenticate", new
            {
                tenantId = owner.TenantId, email = owner.Email, password
            }).ConfigureAwait(false);
            AssertEqual(200, reply.Status, reply.ToString());
            AuthenticateResult result = Read<AuthenticateResult>(reply);
            AssertTrue(result.Success);
            AssertFalse(String.IsNullOrWhiteSpace(result.Token));
            return result.Token!;
        }

        private static async Task<SurfaceReply> AnonymousAsync(SurfaceParityHarness h, HttpMethod method, string path, object? body)
        {
            // Use no API key, including for login. The harness's default administrator client is not reused.
            using (HttpClient client = new HttpClient { BaseAddress = h.Rest.BaseAddress, Timeout = h.Rest.Timeout })
            using (HttpRequestMessage request = new HttpRequestMessage(method, path))
            {
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    return new SurfaceReply("REST", (int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }

        private class PermissionReadback
        {
            /// <summary>Construct the typed permission GET projection.</summary>
            public PermissionReadback() { }
            /// <summary>Scoped mission identifier.</summary>
            public string? Id { get; set; }
            /// <summary>Current launch identity.</summary>
            public DateTime? StartedUtc { get; set; }
            /// <summary>Current source identity.</summary>
            public string? CommitHash { get; set; }
            /// <summary>Optional independent action grants.</summary>
            public MissionAutomaticWorkPermission? AutomaticWorkPermission { get; set; }
            /// <summary>Fail-closed invalid stored component indicator.</summary>
            public bool AutomaticWorkPermissionInvalid { get; set; }
        }

        private sealed class PermissionWriteback : PermissionReadback
        {
            /// <summary>Construct the typed permission POST response projection.</summary>
            public PermissionWriteback() { }
            /// <summary>Mission status returned by the write owner.</summary>
            public MissionStatusEnum Status { get; set; }
            /// <summary>Manual approval timestamp, which a permission write must not set.</summary>
            public DateTime? ReviewedUtc { get; set; }
        }

        private sealed class PermissionAudit
        {
            /// <summary>Construct the HTTP audit projection.</summary>
            public PermissionAudit() { }
            /// <summary>Authenticated actor captured by the service.</summary>
            public string? ActorUserId { get; set; }
            /// <summary>Operation received through the HTTP wire contract.</summary>
            public MissionAutomaticPermissionOperationEnum? Operation { get; set; }
            /// <summary>Independent action received through the HTTP wire contract.</summary>
            public MissionAutomaticActionEnum? Action { get; set; }
        }
    }
}
