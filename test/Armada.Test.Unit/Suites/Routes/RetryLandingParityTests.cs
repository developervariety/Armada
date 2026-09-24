namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A retried landing decides the mission's voyage by the one voyage completion rule on REST and MCP. A voyage that
    /// ended Failed long ago, whose only failed work is then landed by retry-landing, is Complete and raises the
    /// voyage completion hook, however old its failure is.
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
        }
    }
}
