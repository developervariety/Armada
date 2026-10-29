namespace Armada.Test.Unit.Suites.Services
{
    using System.Net.Http;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Per-attempt provenance: the recorder writes who ran a captain process and how it ended, the runtime version
    /// probe is cached and bounded, and REST and MCP read attempts only through the caller's mission scope.
    /// </summary>
    public sealed class MissionAttemptProvenanceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Attempt Provenance";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Recorder_StartThenExit_RecordsCaptainRuntimeModelVersionAndOutcome", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Implement", "work")
                {
                    Persona = "Worker"
                }).ConfigureAwait(false);
                Captain captain = new Captain("claude-1") { Runtime = AgentRuntimeEnum.Codex, Model = "model-x" };

                MissionAttemptProvenance? started = await MissionAttemptProvenanceRecorder.StartAsync(
                    testDb.Driver, mission, captain, 4242, "codex-cli 9.9.9").ConfigureAwait(false);
                AssertNotNull(started, "the launch is recorded");
                AssertEqual(MissionAttemptOutcomeEnum.Running, started!.Outcome);

                bool closed = await MissionAttemptProvenanceRecorder.CloseAsync(testDb.Driver, mission.Id, 4242, 0).ConfigureAwait(false);
                AssertTrue(closed, "the exit closes the running row");

                ProductionFactPage<MissionAttemptProvenance> page = await testDb.Driver.MissionAttempts
                    .EnumerateByMissionAsync(mission.TenantId, mission.Id, 10).ConfigureAwait(false);
                AssertEqual(1, page.Items.Count);
                MissionAttemptProvenance row = page.Items[0];
                AssertEqual(captain.Id, row.CaptainId, "the captain that ran it");
                AssertEqual("Codex", row.Runtime);
                AssertEqual("model-x", row.Model);
                AssertEqual("codex-cli 9.9.9", row.RuntimeVersion);
                AssertEqual("Worker", row.Persona);
                AssertEqual(MissionAttemptOutcomeEnum.Completed, row.Outcome);
                AssertEqual(0, row.ExitCode);
                AssertNotNull(row.EndedUtc, "the end is recorded");
            }).ConfigureAwait(false);

            await RunTest("Recorder_Relaunch_InterruptsTheEarlierRowThatNeverReportedAnExit", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Implement", "work")).ConfigureAwait(false);
                Captain captain = new Captain("claude-1");

                await MissionAttemptProvenanceRecorder.StartAsync(testDb.Driver, mission, captain, 1, null).ConfigureAwait(false);
                await MissionAttemptProvenanceRecorder.StartAsync(testDb.Driver, mission, captain, 2, null).ConfigureAwait(false);
                await MissionAttemptProvenanceRecorder.CloseAsync(testDb.Driver, mission.Id, 1, 0).ConfigureAwait(false);
                await MissionAttemptProvenanceRecorder.CloseAsync(testDb.Driver, mission.Id, 2, 3).ConfigureAwait(false);

                ProductionFactPage<MissionAttemptProvenance> page = await testDb.Driver.MissionAttempts
                    .EnumerateByMissionAsync(mission.TenantId, mission.Id, 10).ConfigureAwait(false);
                AssertEqual(2, page.Items.Count);
                AssertEqual(MissionAttemptOutcomeEnum.Interrupted, page.Items[0].Outcome, "the superseded launch reads Interrupted");
                AssertEqual(MissionAttemptProvenanceRecorder.SupersededReason, page.Items[0].ReasonCode);
                AssertNull(page.Items[0].RuntimeVersion, "an unknown version is empty");
                AssertEqual(MissionAttemptOutcomeEnum.Failed, page.Items[1].Outcome, "a non-zero exit is a failure");
                AssertEqual(3, page.Items[1].ExitCode);
            }).ConfigureAwait(false);

            await RunTest("Recorder_OutcomeOf_MapsExitCodes", () =>
            {
                AssertEqual(MissionAttemptOutcomeEnum.Completed, MissionAttemptProvenanceRecorder.OutcomeOf(0, out string? clean));
                AssertNull(clean);
                AssertEqual(MissionAttemptOutcomeEnum.Interrupted, MissionAttemptProvenanceRecorder.OutcomeOf(null, out string? none));
                AssertEqual(MissionAttemptProvenanceRecorder.ExitUnknownReason, none);
                AssertEqual(MissionAttemptOutcomeEnum.Interrupted, MissionAttemptProvenanceRecorder.OutcomeOf(-1, out string? killed));
                AssertEqual(MissionAttemptProvenanceRecorder.ExitUnknownReason, killed);
                AssertEqual(MissionAttemptOutcomeEnum.Failed, MissionAttemptProvenanceRecorder.OutcomeOf(137, out string? failed));
                AssertEqual(MissionAttemptProvenanceRecorder.ExitNonZeroReason, failed);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("VersionProbe_ProbesOncePerExecutable_AndCachesTheAnswer", async () =>
            {
                int calls = 0;
                RuntimeVersionProbe probe = new RuntimeVersionProbe((command, token) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult<string?>("\n  tool 1.2.3 (build 9)  \nsecond line\n");
                }, null, null);

                string? first = await probe.GetVersionAsync("tool").ConfigureAwait(false);
                string? second = await probe.GetVersionAsync("tool").ConfigureAwait(false);
                string? other = await probe.GetVersionAsync("other").ConfigureAwait(false);

                AssertEqual("tool 1.2.3 (build 9)", first, "the first non-empty line, trimmed");
                AssertEqual(first, second);
                AssertEqual(first, other);
                AssertEqual(2, calls, "one probe per executable name");
                AssertNull(await probe.GetVersionAsync("  ").ConfigureAwait(false), "a blank command has no version");
                AssertEqual(2, calls, "a blank command is never probed");
            }).ConfigureAwait(false);

            await RunTest("VersionProbe_FailureIsCachedBriefly_ThenProbedAgain", async () =>
            {
                int calls = 0;
                DateTime now = new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                RuntimeVersionProbe probe = new RuntimeVersionProbe((command, token) =>
                {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 1) throw new InvalidOperationException("not installed");
                    return Task.FromResult<string?>("tool 2.0");
                }, () => now, null);

                AssertNull(await probe.GetVersionAsync("tool").ConfigureAwait(false), "a failing probe answers no version");
                AssertNull(await probe.GetVersionAsync("tool").ConfigureAwait(false));
                AssertEqual(1, calls, "an empty answer is cached");

                now = now + RuntimeVersionProbe.EmptyLifetime + TimeSpan.FromSeconds(1);
                AssertEqual("tool 2.0", await probe.GetVersionAsync("tool").ConfigureAwait(false), "an empty answer expires");
                now = now + RuntimeVersionProbe.SuccessLifetime - TimeSpan.FromSeconds(1);
                await probe.GetVersionAsync("tool").ConfigureAwait(false);
                AssertEqual(2, calls, "a version answer lives longer than an empty one");
                now = now + TimeSpan.FromSeconds(2);
                await probe.GetVersionAsync("tool").ConfigureAwait(false);
                AssertEqual(3, calls, "a version answer expires");
            }).ConfigureAwait(false);

            await RunTest("VersionProbe_SlowProbe_ReleasesTheCallerAndFillsTheCacheInTheBackground", async () =>
            {
                TaskCompletionSource<string?> gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                int calls = 0;
                RuntimeVersionProbe probe = new RuntimeVersionProbe((command, token) =>
                {
                    Interlocked.Increment(ref calls);
                    return gate.Task;
                }, null, TimeSpan.FromMilliseconds(50));

                AssertNull(await probe.GetVersionAsync("tool").ConfigureAwait(false), "the caller proceeds without a version");
                AssertNull(await probe.GetVersionAsync("tool").ConfigureAwait(false), "a probe still running is not started twice");
                AssertEqual(1, calls);

                gate.SetResult("tool 3.1");
                string? filled = null;
                for (int attempt = 0; attempt < 50 && filled == null; attempt++)
                {
                    filled = await probe.GetVersionAsync("tool").ConfigureAwait(false);
                    if (filled == null) await Task.Delay(20).ConfigureAwait(false);
                }
                AssertEqual("tool 3.1", filled, "the background probe filled the cache");
                AssertEqual(1, calls, "the answer came from the one probe");
            }).ConfigureAwait(false);

            await RunTest("VersionProbe_CancelledCaller_ThrowsWithoutStoppingTheProbe", async () =>
            {
                TaskCompletionSource<string?> gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                RuntimeVersionProbe probe = new RuntimeVersionProbe((command, token) => gate.Task, null, TimeSpan.FromSeconds(30));
                using (CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
                {
                    bool cancelled = false;
                    try { await probe.GetVersionAsync("tool", cancel.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "a cancelled caller stops waiting");
                }
                gate.SetResult("tool 4.0");
                AssertEqual("tool 4.0", await probe.GetVersionAsync("tool").ConfigureAwait(false), "the probe kept running for the next launch");
            }).ConfigureAwait(false);

            await RunTest("VersionProbe_Normalize_BoundsTheStoredLine", () =>
            {
                AssertNull(RuntimeVersionProbe.Normalize(null));
                AssertNull(RuntimeVersionProbe.Normalize(" \r\n "));
                string longLine = new string('v', RuntimeVersionProbe.MaximumVersionLength + 50);
                AssertEqual(RuntimeVersionProbe.MaximumVersionLength, RuntimeVersionProbe.Normalize(longLine)!.Length);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("VersionProbe_RealExecutable_ReadsItsVersionLine_AndAMissingOneIsEmpty", async () =>
            {
                RuntimeVersionProbe probe = new RuntimeVersionProbe();
                string? git = await probe.GetVersionAsync("git").ConfigureAwait(false);
                AssertNotNull(git, "the real probe runs git --version");
                AssertTrue(git!.StartsWith("git version", StringComparison.Ordinal), git);
                AssertNull(await probe.GetVersionAsync("armada-no-such-runtime-binary").ConfigureAwait(false), "a missing executable has no version");
            }).ConfigureAwait(false);

            await RunTest("Attempts_RestAndMcpRead_AreScopedToTheCallersMission_AndShareOneAnswer", async () =>
            {
                using SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false);
                Mission own = await harness.Driver.Missions.CreateAsync(new Mission("Own", "work")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                Captain captain = new Captain("claude-1") { Runtime = AgentRuntimeEnum.ClaudeCode, Model = "model-x" };
                await MissionAttemptProvenanceRecorder.StartAsync(harness.Driver, own, captain, 11, "claude 1.0").ConfigureAwait(false);
                await MissionAttemptProvenanceRecorder.CloseAsync(harness.Driver, own.Id, 11, 0).ConfigureAwait(false);
                await MissionAttemptProvenanceRecorder.StartAsync(harness.Driver, own, captain, 12, "claude 1.0").ConfigureAwait(false);

                SurfaceReply rest = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + own.Id + "/attempts").ConfigureAwait(false);
                AssertEqual(200, rest.Status, rest.ToString());
                MissionAttemptProvenanceReport restReport = Parse(rest);
                AssertEqual(own.Id, restReport.MissionId);
                AssertEqual(2, restReport.Attempts.Count);
                AssertEqual(MissionAttemptOutcomeEnum.Completed, restReport.Attempts[0].Outcome);
                AssertEqual(MissionAttemptOutcomeEnum.Running, restReport.Attempts[1].Outcome);
                AssertEqual("claude 1.0", restReport.Attempts[0].RuntimeVersion);

                SurfaceReply mcp = await harness.McpAsync(McpMissionAttemptTools.ToolName, new { missionId = own.Id }).ConfigureAwait(false);
                AssertEqual(200, mcp.Status, mcp.ToString());
                MissionAttemptProvenanceReport mcpReport = Parse(mcp);
                AssertEqual(String.Join(",", restReport.Attempts.Select(item => item.Id)), String.Join(",", mcpReport.Attempts.Select(item => item.Id)), "REST and MCP read the same attempts");

                SurfaceReply limited = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + own.Id + "/attempts?limit=1").ConfigureAwait(false);
                MissionAttemptProvenanceReport limitedReport = Parse(limited);
                AssertEqual(1, limitedReport.Attempts.Count);
                AssertTrue(limitedReport.Truncated, "older attempts beyond the limit are named");
                AssertEqual(12, limitedReport.Attempts[0].ProcessId, "the newest attempt is kept");

                string missionToken = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, own.Id);
                SurfaceReply tokenReply = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + own.Id + "/attempts", null, missionToken).ConfigureAwait(false);
                AssertEqual(200, tokenReply.Status, "a mission session token reads its own mission's attempts: " + tokenReply);
                Mission sibling = await harness.Driver.Missions.CreateAsync(new Mission("Sibling", "work")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                SurfaceReply siblingReply = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + sibling.Id + "/attempts", null, missionToken).ConfigureAwait(false);
                AssertEqual(404, siblingReply.Status, "a mission session token never reads another mission's attempts: " + siblingReply);

                AssertEqual(400, (await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + own.Id + "/attempts?limit=0").ConfigureAwait(false)).Status, "a limit below one is refused");
                AssertEqual(404, (await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/msn_absent/attempts").ConfigureAwait(false)).Status, "an unknown mission is not found");

                // The administrator of another tenant, a same-tenant user who does not own the mission, and a
                // mission credential issued to a different mission all read nothing.
                Dictionary<string, Func<JsonElement?, Task<object>>> raw = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                McpMissionAttemptTools.Register((name, _, _, handler) => { raw[name] = handler; }, harness.Driver);
                AssertNull(await ReadRawAsync(raw, AuthContext.Authenticated("ten_elsewhere", "usr_elsewhere", false, true, "Bearer"), own.Id).ConfigureAwait(false), "another tenant's administrator reads nothing");
                AssertNull(await ReadRawAsync(raw, AuthContext.Authenticated(Constants.DefaultTenantId, "usr_someone_else", false, false, "Bearer"), own.Id).ConfigureAwait(false), "a same-tenant user who does not own the mission reads nothing");
                AssertNotNull(await ReadRawAsync(raw, AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer"), own.Id).ConfigureAwait(false), "the owner reads their mission");
                AssertNotNull(await ReadRawAsync(raw, AuthContext.Authenticated(Constants.DefaultTenantId, "usr_someone_else", false, true, "Bearer"), own.Id).ConfigureAwait(false), "a tenant administrator reads a mission of their tenant");
                AuthContext otherMissionCredential = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Bearer");
                otherMissionCredential.MissionId = "msn_some_other_mission";
                AssertNull(await ReadRawAsync(raw, otherMissionCredential, own.Id).ConfigureAwait(false), "a mission credential reads only its own mission");

                AssertFalse(McpToolAccessPolicy.IsAllowed(otherMissionCredential, McpMissionAttemptTools.ToolName), "attempt provenance is not offered to mission callers");
            }).ConfigureAwait(false);
        }

        private static MissionAttemptProvenanceReport Parse(SurfaceReply reply)
        {
            JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return JsonSerializer.Deserialize<MissionAttemptProvenanceReport>(reply.Body, options)!;
        }

        private static async Task<MissionAttemptProvenanceReport?> ReadRawAsync(Dictionary<string, Func<JsonElement?, Task<object>>> raw, AuthContext caller, string missionId)
        {
            using (McpCallerContext.Begin(caller))
            {
                object result = await raw[McpMissionAttemptTools.ToolName](JsonSerializer.SerializeToElement(new { missionId = missionId })).ConfigureAwait(false);
                return result as MissionAttemptProvenanceReport;
            }
        }
    }
}
