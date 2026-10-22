namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A retried landing decides the mission's voyage by the one voyage completion rule on REST and MCP. A voyage that
    /// ended Failed long ago, whose only failed work is then landed by retry-landing, is Complete and raises the
    /// voyage completion hook, however old its failure is. MCP runs the retry as a job and answers at once; one retry
    /// per mission runs at a time, so a repeated MCP request joins the running job and REST refuses a second retry.
    /// </summary>
    public class RetryLandingParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Retry Landing Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("RetryLanding_LandsTheOnlyFailedWorkOfAnOldFailedVoyage_CompletesTheVoyageOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    harness.Landing.OnPerformLanding = async (mission, dock) =>
                    {
                        Mission? landed = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        landed!.Status = MissionStatusEnum.Complete;
                        landed.CompletedUtc = DateTime.UtcNow;
                        await harness.Driver.Missions.UpdateAsync(landed).ConfigureAwait(false);
                    };

                    foreach (string surface in new[] { "REST", "MCP" })
                    {
                        DateTime failedAt = DateTime.UtcNow.AddDays(-3);
                        Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("retry-vessel-" + surface, "https://github.com/test/retry.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            LocalPath = harness.MakeDirectory("repo-" + surface)
                        }).ConfigureAwait(false);
                        Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("retry " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = VoyageStatusEnum.Failed,
                            CompletedUtc = failedAt
                        }).ConfigureAwait(false);
                        string branch = "armada/retry-" + surface;
                        harness.Git.ExistingBranches.Add(branch);
                        Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("retry " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VesselId = vessel.Id,
                            VoyageId = voyage.Id,
                            BranchName = branch,
                            Status = MissionStatusEnum.LandingFailed,
                            CompletedUtc = failedAt
                        }).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = surface == "REST"
                            ? await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + mission.Id + "/retry-landing", new { }).ConfigureAwait(false)
                            : await harness.McpAsync("armada_retry_landing", new { missionId = mission.Id }).ConfigureAwait(false);
                        if (surface == "MCP")
                        {
                            AssertEqual(200, reply.Status, "MCP: the retry is accepted as a job: " + reply);
                            reply = await WaitForJobAsync(harness, JobIdOf(reply)).ConfigureAwait(false);
                            AssertContains("Succeeded", reply.Body, "MCP: the retry job succeeds: " + reply);
                        }

                        Mission? storedMission = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Complete, storedMission!.Status, surface + ": the retried landing lands the work: " + reply);
                        Voyage? storedVoyage = await harness.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                        AssertEqual(VoyageStatusEnum.Complete, storedVoyage!.Status,
                            surface + ": a Failed voyage whose only failed work has landed is Complete, however old the failure");
                        AssertTrue(harness.Broadcasts.Contains("voyage:" + voyage.Id + ":Complete"),
                            surface + ": the voyage completion hook is raised: " + String.Join(",", harness.Broadcasts));
                    }
                }
            }).ConfigureAwait(false);

            // A landing can outlast the MCP client deadline. A caller that lost the reply and retried again started a
            // second landing of the same work while the first still ran.
            await RunTest("RetryLanding_WhileOneRuns_McpJoinsTheRunningJobAndRestIsRefused_AndTheWorkLandsOnce", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<bool> landingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int landings = 0;
                    harness.Landing.OnPerformLanding = async (mission, dock) =>
                    {
                        Interlocked.Increment(ref landings);
                        landingStarted.TrySetResult(true);
                        await release.Task.ConfigureAwait(false);
                        Mission? landed = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        landed!.Status = MissionStatusEnum.Complete;
                        landed.CompletedUtc = DateTime.UtcNow;
                        await harness.Driver.Missions.UpdateAsync(landed).ConfigureAwait(false);
                    };

                    Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("retry-once-vessel", "https://github.com/test/retry-once.git")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        LocalPath = harness.MakeDirectory("repo-once")
                    }).ConfigureAwait(false);
                    const string branch = "armada/retry-once";
                    harness.Git.ExistingBranches.Add(branch);
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("retry once")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        BranchName = branch,
                        Status = MissionStatusEnum.LandingFailed
                    }).ConfigureAwait(false);

                    Task<SurfaceReply> first = harness.McpAsync("armada_retry_landing", new { missionId = mission.Id });
                    string jobId;
                    try
                    {
                        Task finishedFirst = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
                        AssertTrue(finishedFirst == first, "MCP answers while the landing still runs, instead of holding the caller until it ends");
                        SurfaceReply accepted = await first.ConfigureAwait(false);
                        jobId = JobIdOf(accepted);
                        AssertContains("\"joined\":false", accepted.Body, "the first request starts a job: " + accepted);
                        AssertTrue(await Task.WhenAny(landingStarted.Task, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false) == landingStarted.Task,
                            "the job runs the landing");

                        SurfaceReply repeated = await harness.McpAsync("armada_retry_landing", new { missionId = mission.Id }).ConfigureAwait(false);
                        AssertEqual(jobId, JobIdOf(repeated), "a repeated MCP request returns the running job: " + repeated);
                        AssertContains("\"joined\":true", repeated.Body, "and says it joined it: " + repeated);

                        SurfaceReply rest = await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + mission.Id + "/retry-landing", new { }).ConfigureAwait(false);
                        AssertEqual(409, rest.Status, "REST refuses a second retry while one runs: " + rest);
                        AssertContains("already running", rest.Body, "and names the running retry: " + rest);
                    }
                    finally
                    {
                        release.TrySetResult(true);
                    }

                    SurfaceReply done = await WaitForJobAsync(harness, jobId).ConfigureAwait(false);
                    AssertContains("Succeeded", done.Body, "the retry job succeeds: " + done);
                    AssertEqual(1, landings, "the work lands once");
                    Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, stored!.Status, "the mission is Complete");
                }
            }).ConfigureAwait(false);
        }

        private static string JobIdOf(SurfaceReply reply)
        {
            using (JsonDocument document = JsonDocument.Parse(reply.Body))
            {
                if (!document.RootElement.TryGetProperty("jobId", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("the reply carries no job id: " + reply.Body);
                return id.GetString()!;
            }
        }

        private static async Task<SurfaceReply> WaitForJobAsync(SurfaceParityHarness harness, string jobId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            SurfaceReply status = await harness.McpAsync("armada_job_status", new { jobId = jobId }).ConfigureAwait(false);
            while (DateTime.UtcNow < deadline
                && !status.Body.Contains("Succeeded", StringComparison.Ordinal)
                && !status.Body.Contains("Failed", StringComparison.Ordinal)
                && !status.Body.Contains("Lost", StringComparison.Ordinal))
            {
                await Task.Delay(25).ConfigureAwait(false);
                status = await harness.McpAsync("armada_job_status", new { jobId = jobId }).ConfigureAwait(false);
            }
            return status;
        }
    }
}
