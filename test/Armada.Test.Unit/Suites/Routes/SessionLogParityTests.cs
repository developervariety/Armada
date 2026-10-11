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
