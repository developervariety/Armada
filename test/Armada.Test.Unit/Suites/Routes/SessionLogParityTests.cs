namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A mission log and a captain log read the same on REST, WebSocket and MCP: one path resolution (a mission's
    /// newest non-empty sidecar log when its canonical log is empty or absent), one page clamp, and one secret
    /// redaction. A key-shaped value written by an agent never leaves the server on any surface.
    /// </summary>
    public class SessionLogParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Session Log Parity";

        /// <summary>A GitHub-token-shaped value built at run time, so no committed line has a real token's shape.</summary>
        private static string TokenShapedValue()
        {
            return "gh" + "p_" + new string('A', 12) + "b1C2d3E4f5G6h7I8j9K0";
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TenantCreate_WhitespaceId_IsRejectedWithoutCreatingRows", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    EnumerationQuery query = new EnumerationQuery();
                    long tenantsBefore = (await harness.Driver.Tenants.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords;
                    long usersBefore = (await harness.Driver.Users.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords;
                    long credentialsBefore = (await harness.Driver.Credentials.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords;

                    object[] invalidRequests =
                    {
                        new { Id = " ", Name = "ASCII space" },
                        new { Id = "\t ", Name = "ASCII whitespace" },
                        new { Id = "\u00a0", Name = "No-break space" },
                        new { Id = "\u2003", Name = "Em space" },
                        new { Id = "", Name = "Empty ID" },
                        new { Id = (string?)null, Name = "Null ID" }
                    };
                    foreach (object invalidRequest in invalidRequests)
                    {
                        SurfaceReply rejected = await harness.RestAsync(HttpMethod.Post, "/api/v1/tenants", invalidRequest).ConfigureAwait(false);
                        AssertEqual(400, rejected.Status, rejected.Body);
                        AssertEqual(tenantsBefore, (await harness.Driver.Tenants.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords);
                        AssertEqual(usersBefore, (await harness.Driver.Users.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords);
                        AssertEqual(credentialsBefore, (await harness.Driver.Credentials.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords);
                    }

                    SurfaceReply generatedId = await harness.RestAsync(HttpMethod.Post, "/api/v1/tenants", new { Name = "Generated ID tenant" }).ConfigureAwait(false);
                    AssertEqual(201, generatedId.Status, generatedId.Body);
                    SurfaceReply validId = await harness.RestAsync(HttpMethod.Post, "/api/v1/tenants", new { Id = "ten_route_valid", Name = "Valid ID tenant" }).ConfigureAwait(false);
                    AssertEqual(201, validId.Status, validId.Body);
                    AssertEqual(tenantsBefore + 2, (await harness.Driver.Tenants.EnumerateAsync(query).ConfigureAwait(false)).TotalRecords);
                    UserMaster? validTenantAdmin = await harness.Driver.Users.ReadByEmailAsync("ten_route_valid", Constants.DefaultUserEmail).ConfigureAwait(false);
                    AssertNotNull(validTenantAdmin, "The accepted valid ID owns its seeded admin user");
                    AssertEqual("ten_route_valid", validTenantAdmin!.TenantId);

                    SurfaceReply updateWhitespace = await harness.RestAsync(HttpMethod.Put, "/api/v1/tenants/ten_route_valid", new { Id = "\t  ", Name = "Invalid update" }).ConfigureAwait(false);
                    AssertEqual(400, updateWhitespace.Status, updateWhitespace.Body);
                    AssertEqual("Valid ID tenant", (await harness.Driver.Tenants.ReadAsync("ten_route_valid").ConfigureAwait(false))!.Name);
                }
            }).ConfigureAwait(false);

            await RunTest("MissionLog_AdminOwnedMissionToken_CannotReadUnrelatedLog", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    UserMaster? owner = await harness.Driver.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false);
                    AssertNotNull(owner, "The token owner exists");
                    AssertTrue(owner!.IsAdmin, "The regression must use an administrator-owned mission token");
                    Mission own = await CreateMissionAsync(harness).ConfigureAwait(false);
                    Mission unrelated = await CreateMissionAsync(harness).ConfigureAwait(false);
                    harness.WriteLogFile(Path.Combine("missions", own.Id + ".log"), "own mission evidence");
                    harness.WriteLogFile(Path.Combine("missions", unrelated.Id + ".log"), "unrelated private evidence");
                    string token = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, own.Id);

                    SurfaceReply allowed = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + own.Id + "/log", sessionToken: token).ConfigureAwait(false);
                    AssertEqual(200, allowed.Status);
                    AssertContains("own mission evidence", allowed.Body);
                    SurfaceReply denied = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + unrelated.Id + "/log", sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, denied.Status, "An owner's administrator role must not widen a mission token");
                    AssertFalse(denied.Body.Contains("unrelated private evidence", StringComparison.Ordinal));
                    SurfaceReply operatorRead = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + unrelated.Id + "/log").ConfigureAwait(false);
                    AssertEqual(200, operatorRead.Status);
                    AssertContains("unrelated private evidence", operatorRead.Body);
                    SurfaceReply invalid = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + own.Id + "/log", sessionToken: "invalid-session").ConfigureAwait(false);
                    AssertEqual(401, invalid.Status, "An invalid token must not fall back to the harness operator key");
                }
            }).ConfigureAwait(false);

            await RunTest("MissionReads_AdminOwnedMissionToken_StaysWithinMissionAndVoyageScope", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Voyage ownVoyage = await harness.Driver.Voyages.CreateAsync(new Voyage("mission read scope")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    Voyage foreignVoyage = await harness.Driver.Voyages.CreateAsync(new Voyage("foreign mission read scope")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    Mission dependency = await CreateMissionAsync(harness).ConfigureAwait(false);
                    Mission parent = await CreateMissionAsync(harness).ConfigureAwait(false);
                    Mission anchor = await CreateMissionAsync(harness).ConfigureAwait(false);
                    anchor.VoyageId = ownVoyage.Id;
                    anchor.DependsOnMissionId = dependency.Id;
                    anchor.ParentMissionId = parent.Id;
                    await harness.Driver.Missions.UpdateAsync(anchor).ConfigureAwait(false);
                    Mission sibling = await CreateMissionAsync(harness).ConfigureAwait(false);
                    sibling.VoyageId = ownVoyage.Id;
                    await harness.Driver.Missions.UpdateAsync(sibling).ConfigureAwait(false);
                    Mission unrelated = await CreateMissionAsync(harness).ConfigureAwait(false);
                    unrelated.VoyageId = foreignVoyage.Id;
                    await harness.Driver.Missions.UpdateAsync(unrelated).ConfigureAwait(false);
                    UserMaster otherOwner = await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "mission-read-other@example.com", "password")).ConfigureAwait(false);
                    Mission otherOwnerMission = await harness.Driver.Missions.CreateAsync(new Mission("other voyage member")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = otherOwner.Id,
                        VoyageId = ownVoyage.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Voyage crossOwnerVoyage = await harness.Driver.Voyages.CreateAsync(new Voyage("cross-owner linked voyage")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = otherOwner.Id
                    }).ConfigureAwait(false);
                    Mission crossOwnerAnchor = await harness.Driver.Missions.CreateAsync(new Mission("cross-owner voyage anchor")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VoyageId = crossOwnerVoyage.Id
                    }).ConfigureAwait(false);
                    TenantMetadata foreignTenant = await harness.Driver.Tenants.CreateAsync(new TenantMetadata("mission-read-foreign-tenant")).ConfigureAwait(false);
                    UserMaster foreignTenantOwner = await harness.Driver.Users.CreateAsync(new UserMaster(foreignTenant.Id, "mission-read-foreign-tenant@example.com", "password")).ConfigureAwait(false);
                    Mission foreignTenantMission = await harness.Driver.Missions.CreateAsync(new Mission("foreign tenant member of same voyage")
                    {
                        TenantId = foreignTenant.Id,
                        UserId = foreignTenantOwner.Id,
                        VoyageId = ownVoyage.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Vessel rescueVessel = await harness.Driver.Vessels.CreateAsync(new Vessel("mission-read-rescue", "https://example.invalid/mission-read-rescue.git")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    Mission failedParent = await harness.Driver.Missions.CreateAsync(new Mission("rescue chain failed parent")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = rescueVessel.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Mission rescueRoot = await harness.Driver.Missions.CreateAsync(new Mission("rescue root")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VoyageId = ownVoyage.Id,
                        VesselId = rescueVessel.Id,
                        ParentMissionId = failedParent.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);
                    Mission rescueStage = await harness.Driver.Missions.CreateAsync(new Mission("rescue review")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VoyageId = ownVoyage.Id,
                        VesselId = rescueVessel.Id,
                        DependsOnMissionId = rescueRoot.Id,
                        Description = RescueMissionMarker.Marker
                    }).ConfigureAwait(false);

                    string token = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, anchor.Id);
                    foreach (string collectionRoute in new[]
                    {
                        "/api/v1/missions",
                        "/api/v1/missions/summaries",
                        "/api/v1/missions/history",
                        "/api/v1/voyages"
                    })
                    {
                        SurfaceReply deniedCollection = await harness.RestAsync(HttpMethod.Get, collectionRoute, sessionToken: token).ConfigureAwait(false);
                        AssertEqual(403, deniedCollection.Status, "Mission credentials cannot enumerate an unbounded collection: " + collectionRoute);
                    }
                    foreach (string collectionRoute in new[]
                    {
                        "/api/v1/missions/enumerate",
                        "/api/v1/missions/summaries/enumerate",
                        "/api/v1/voyages/enumerate"
                    })
                    {
                        SurfaceReply deniedCollection = await harness.RestAsync(HttpMethod.Post, collectionRoute, new { }, token).ConfigureAwait(false);
                        AssertEqual(403, deniedCollection.Status, "Mission credentials cannot enumerate an unbounded collection: " + collectionRoute);
                    }
                    foreach (Mission allowedMission in new[] { anchor, dependency, parent, sibling })
                    {
                        SurfaceReply allowed = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + allowedMission.Id, sessionToken: token).ConfigureAwait(false);
                        AssertEqual(200, allowed.Status, "The mission token may read its " + (allowedMission.Id == anchor.Id ? "anchor" : "linked report") + ": " + allowed);
                        AssertContains(allowedMission.Id, allowed.Body);
                    }

                    SurfaceReply deniedMission = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + unrelated.Id, sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, deniedMission.Status, "The token owner's administrator role does not widen mission detail reads");
                    AssertFalse(deniedMission.Body.Contains(unrelated.Title, StringComparison.Ordinal));
                    SurfaceReply deniedSameVoyageOwner = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + otherOwnerMission.Id, sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, deniedSameVoyageOwner.Status, "A same-voyage mission from another user stays outside the token scope");
                    SurfaceReply deniedSameVoyageTenant = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + foreignTenantMission.Id, sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, deniedSameVoyageTenant.Status, "A same-voyage mission from another tenant stays outside the token scope");

                    string rescueToken = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, rescueStage.Id);
                    SurfaceReply rescueParentRead = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + failedParent.Id, sessionToken: rescueToken).ConfigureAwait(false);
                    AssertEqual(200, rescueParentRead.Status, "A final rescue stage keeps the shared rescue-chain read contract");

                    SurfaceReply ownVoyageRead = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + ownVoyage.Id, sessionToken: token).ConfigureAwait(false);
                    AssertEqual(200, ownVoyageRead.Status, "The token may read its anchor voyage: " + ownVoyageRead);
                    AssertContains(anchor.Id, ownVoyageRead.Body);
                    AssertFalse(ownVoyageRead.Body.Contains(otherOwnerMission.Id, StringComparison.Ordinal), "A mission token cannot widen the associated mission list to other owners");

                    SurfaceReply ownVoyageSummary = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + ownVoyage.Id + "/mission-summary", sessionToken: token).ConfigureAwait(false);
                    AssertEqual(200, ownVoyageSummary.Status, "The token may read counts for its anchor voyage: " + ownVoyageSummary);
                    AssertContains("\"Failed\":2", ownVoyageSummary.Body, "Voyage counts include only missions owned by the token principal");

                    SurfaceReply deniedVoyage = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + foreignVoyage.Id, sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, deniedVoyage.Status, "The token cannot read another voyage owned by its administrator principal");
                    AssertFalse(deniedVoyage.Body.Contains(foreignVoyage.Title, StringComparison.Ordinal));
                    SurfaceReply deniedVoyageSummary = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + foreignVoyage.Id + "/mission-summary", sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, deniedVoyageSummary.Status, "The token cannot read counts for another voyage owned by its administrator principal");
                    string crossOwnerToken = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, crossOwnerAnchor.Id);
                    SurfaceReply deniedCrossOwnerVoyage = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + crossOwnerVoyage.Id, sessionToken: crossOwnerToken).ConfigureAwait(false);
                    AssertEqual(404, deniedCrossOwnerVoyage.Status, "A mission cannot use a voyage owned by another user in the same tenant");

                    SurfaceReply operatorRead = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + unrelated.Id).ConfigureAwait(false);
                    AssertEqual(200, operatorRead.Status, "A request without a mission token keeps administrator scope");
                    SurfaceReply operatorVoyageRead = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + foreignVoyage.Id).ConfigureAwait(false);
                    AssertEqual(200, operatorVoyageRead.Status, "A request without a mission token keeps administrator voyage scope");

                    Voyage legacyVoyage = await harness.Driver.Voyages.CreateAsync(new Voyage("legacy default-owned voyage")
                    {
                        TenantId = null,
                        UserId = null
                    }).ConfigureAwait(false);
                    Mission legacyMember = await harness.Driver.Missions.CreateAsync(new Mission("legacy default-owned member")
                    {
                        TenantId = null,
                        UserId = null,
                        VoyageId = legacyVoyage.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Mission legacyAnchor = await harness.Driver.Missions.CreateAsync(new Mission("legacy default-owned anchor")
                    {
                        TenantId = null,
                        UserId = null,
                        VoyageId = legacyVoyage.Id,
                        Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);
                    Voyage? storedLegacyVoyage = await harness.Driver.Voyages.ReadAsync(legacyVoyage.Id).ConfigureAwait(false);
                    Mission? storedLegacyMember = await harness.Driver.Missions.ReadAsync(legacyMember.Id).ConfigureAwait(false);
                    AssertNull(storedLegacyVoyage!.TenantId, "The legacy voyage fixture has a null tenant");
                    AssertNull(storedLegacyVoyage.UserId, "The legacy voyage fixture has a null user");
                    AssertNull(storedLegacyMember!.TenantId, "The legacy mission fixture has a null tenant");
                    AssertNull(storedLegacyMember.UserId, "The legacy mission fixture has a null user");
                    string legacyToken = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, legacyAnchor.Id);
                    SurfaceReply legacyVoyageRead = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + legacyVoyage.Id, sessionToken: legacyToken).ConfigureAwait(false);
                    AssertEqual(200, legacyVoyageRead.Status, "Null owner fields resolve to the default owner for an anchor voyage");
                    AssertContains(legacyMember.Id, legacyVoyageRead.Body, "The voyage detail keeps null-owner missions of the default owner");
                    SurfaceReply legacyVoyageSummary = await harness.RestAsync(HttpMethod.Get, "/api/v1/voyages/" + legacyVoyage.Id + "/mission-summary", sessionToken: legacyToken).ConfigureAwait(false);
                    AssertEqual(200, legacyVoyageSummary.Status, "The token may read counts for a null-owner anchor voyage");
                    AssertContains("\"Failed\":1", legacyVoyageSummary.Body, "Counts include null-owner missions of the default owner");
                }
            }).ConfigureAwait(false);

            await RunTest("MissionLog_CapturePages_PreserveOutputAndEnforceMissionScope", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission own = await CreateMissionAsync(harness).ConfigureAwait(false);
                    Mission unrelated = await CreateMissionAsync(harness).ConfigureAwait(false);
                    string output = "first🙂" + new string('x', 70000) + "\nCITATION OFFENDER: ExampleComponent\nlast";
                    MissionLogCaptureMetadata capture = await MissionLogCaptureArtifact.WriteAsync(harness.Settings.LogDirectory, own.Id, output).ConfigureAwait(false);
                    MissionLogCaptureMetadata foreignCapture = await MissionLogCaptureArtifact.WriteAsync(harness.Settings.LogDirectory, unrelated.Id, "unrelated capture evidence").ConfigureAwait(false);
                    string token = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, own.Id);
                    string route = "/api/v1/missions/" + own.Id + "/log?captureId=" + capture.CaptureId;
                    StringBuilder joined = new StringBuilder();
                    long offset = 0;
                    MissionLogCapturePage page;
                    do
                    {
                        SurfaceReply reply = await harness.RestAsync(HttpMethod.Get, route + "&offsetBytes=" + offset + "&lengthBytes=4096&sha256=" + capture.Sha256, sessionToken: token).ConfigureAwait(false);
                        AssertEqual(200, reply.Status);
                        page = JsonHelper.Deserialize<MissionLogCapturePage>(reply.Body);
                        AssertEqual(offset, page.OffsetBytes);
                        AssertTrue(page.LengthBytes > 0 || !page.HasMore, "Each page makes progress");
                        AssertTrue(page.LengthBytes <= 4096);
                        AssertTrue(page.Complete);
                        joined.Append(page.Content);
                        offset = page.OffsetBytes + page.LengthBytes;
                    }
                    while (page.HasMore);
                    AssertEqual(output, joined.ToString(), "The route preserves the long line, UTF-8 text and failure detail");
                    AssertEqual(capture.TotalUtf8Bytes, offset);
                    AssertEqual(capture.Sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString()))).ToLowerInvariant());

                    string foreignRoute = "/api/v1/missions/" + unrelated.Id + "/log?captureId=" + foreignCapture.CaptureId;
                    SurfaceReply denied = await harness.RestAsync(HttpMethod.Get, foreignRoute, sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, denied.Status);
                    AssertFalse(denied.Body.Contains("unrelated capture evidence", StringComparison.Ordinal));
                    SurfaceReply operatorRead = await harness.RestAsync(HttpMethod.Get, foreignRoute).ConfigureAwait(false);
                    AssertEqual(200, operatorRead.Status);
                    AssertContains("unrelated capture evidence", operatorRead.Body);
                    SurfaceReply deniedBeforeValidation = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + unrelated.Id + "/log?captureId=invalid&offsetBytes=-1", sessionToken: token).ConfigureAwait(false);
                    AssertEqual(404, deniedBeforeValidation.Status, "Mission scope is checked before capture validation or file access");

                    foreach (string query in new[] { "&offsetBytes=not-an-integer", "&offsetBytes=-1", "&lengthBytes=0", "&lengthBytes=64001" })
                    {
                        SurfaceReply invalid = await harness.RestAsync(HttpMethod.Get, route + query, sessionToken: token).ConfigureAwait(false);
                        AssertEqual(400, invalid.Status, "Invalid capture page: " + query);
                    }
                    SurfaceReply mismatched = await harness.RestAsync(HttpMethod.Get, route + "&sha256=" + new string('0', 64), sessionToken: token).ConfigureAwait(false);
                    AssertEqual(409, mismatched.Status);
                }
            }).ConfigureAwait(false);

            await RunTest("MissionLog_KeyShapedValue_IsRedactedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(harness).ConfigureAwait(false);
                    string shaped = TokenShapedValue();
                    harness.WriteLogFile(Path.Combine("missions", mission.Id + ".log"),
                        "starting work\nusing credential " + shaped + " for push\ndone\n");

                    foreach (SurfaceReply reply in await ReadMissionLogAsync(harness, mission.Id, 50, 0).ConfigureAwait(false))
                    {
                        AssertFalse(reply.Refused, "the log is readable: " + reply);
                        AssertFalse(reply.Body.Contains(shaped, StringComparison.Ordinal), "a key-shaped value is redacted: " + reply);
                        AssertContains("REDACTED", reply.Body, "the redaction marker replaces it: " + reply);
                        AssertContains("starting work", reply.Body, "the rest of the log is returned: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionLog_OnlyASidecarLog_IsReadOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(harness).ConfigureAwait(false);
                    harness.WriteLogFile(Path.Combine("missions", mission.Id + ".log"), "");
                    harness.WriteLogFile(Path.Combine("missions", mission.Id + ".attempt2.log"), "sidecar attempt line\n");

                    foreach (SurfaceReply reply in await ReadMissionLogAsync(harness, mission.Id, 50, 0).ConfigureAwait(false))
                    {
                        AssertFalse(reply.Refused, "the log is readable: " + reply);
                        AssertContains("sidecar attempt line", reply.Body, "the newest non-empty sidecar is read when the canonical log is empty: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionLog_NegativeOffset_IsClampedAlikeOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(harness).ConfigureAwait(false);
                    harness.WriteLogFile(Path.Combine("missions", mission.Id + ".log"), "line one\nline two\nline three\n");

                    foreach (SurfaceReply reply in await ReadMissionLogAsync(harness, mission.Id, 2, -5).ConfigureAwait(false))
                    {
                        AssertFalse(reply.Refused, "the log is readable: " + reply);
                        AssertContains("line one", reply.Body, "a negative offset starts at the first line: " + reply);
                        AssertContains("line two", reply.Body, "the page holds the requested line count: " + reply);
                        AssertFalse(reply.Body.Contains("line three", StringComparison.Ordinal), "the page stops at the requested line count: " + reply);
                        AssertContains("\"totalLines\":3", reply.Body.Replace("TotalLines", "totalLines"), "every surface counts the same lines: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CaptainLog_KeyShapedValue_IsRedactedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("parity-log-captain")).ConfigureAwait(false);
                    string shaped = TokenShapedValue();
                    string sessionLog = harness.WriteLogFile(Path.Combine("captains", captain.Id + ".session.log"),
                        "captain started\nexport TOKEN=" + shaped + "\ncaptain idle\n");
                    harness.WriteLogFile(Path.Combine("captains", captain.Id + ".current"), sessionLog);

                    List<SurfaceReply> replies = new List<SurfaceReply>
                    {
                        await harness.RestAsync(HttpMethod.Get, "/api/v1/captains/" + captain.Id + "/log?lines=50").ConfigureAwait(false),
                        await harness.WebSocketAsync("get_captain_log", captain.Id, lines: 50).ConfigureAwait(false),
                        await harness.McpAsync("armada_get_captain_log", new { captainId = captain.Id, lines = 50 }).ConfigureAwait(false)
                    };
                    foreach (SurfaceReply reply in replies)
                    {
                        AssertFalse(reply.Refused, "the log is readable: " + reply);
                        AssertFalse(reply.Body.Contains(shaped, StringComparison.Ordinal), "a key-shaped value is redacted: " + reply);
                        AssertContains("REDACTED", reply.Body, "the redaction marker replaces it: " + reply);
                        AssertContains("captain started", reply.Body, "the rest of the log is returned: " + reply);
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(SurfaceParityHarness harness)
        {
            return await harness.Driver.Missions.CreateAsync(new Mission("parity log mission")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                Status = MissionStatusEnum.Failed
            }).ConfigureAwait(false);
        }

        private static async Task<List<SurfaceReply>> ReadMissionLogAsync(SurfaceParityHarness harness, string missionId, int lines, int offset)
        {
            return new List<SurfaceReply>
            {
                await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + missionId + "/log?lines=" + lines + "&offset=" + offset).ConfigureAwait(false),
                await harness.WebSocketAsync("get_mission_log", missionId, lines: lines, offset: offset).ConfigureAwait(false),
                await harness.McpAsync("armada_get_mission_log", new { missionId = missionId, lines = lines, offset = offset }).ConfigureAwait(false)
            };
        }
    }
}
