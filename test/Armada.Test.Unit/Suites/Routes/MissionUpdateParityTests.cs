namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Updating a mission's metadata is one operation on REST, WebSocket and MCP. Each surface changes only the fields
    /// the request names, so a partial body keeps the rest; the persona is a metadata field on every surface; a
    /// dependency that names no visible mission is refused; and an empty dependency clears it.
    /// </summary>
    public class MissionUpdateParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Update Parity";

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("UpdateMission_PartialBody_KeepsTheFieldsItOmitsOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface).ConfigureAwait(false);
                        SurfaceReply reply = await UpdateAsync(harness, surface, mission.Id,
                            new Dictionary<string, object?> { { "Priority", 7 } }).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a partial update is accepted: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(7, stored!.Priority, surface + ": the named field changes");
                        AssertEqual("title " + surface, stored.Title, surface + ": an omitted title is kept");
                        AssertEqual("description " + surface, stored.Description, surface + ": an omitted description is kept");
                        AssertEqual("Worker", stored.Persona, surface + ": an omitted persona is kept");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateMission_Persona_IsStoredOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface).ConfigureAwait(false);
                        SurfaceReply reply = await UpdateAsync(harness, surface, mission.Id,
                            new Dictionary<string, object?> { { "Persona", "Judge" } }).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a persona update is accepted: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual("Judge", stored!.Persona, surface + ": the persona is stored");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateMission_DependencyOnNoVisibleMission_IsRefusedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface).ConfigureAwait(false);
                        SurfaceReply reply = await UpdateAsync(harness, surface, mission.Id,
                            new Dictionary<string, object?> { { "DependsOnMissionId", "msn_missing_" + surface } }).ConfigureAwait(false);

                        AssertTrue(reply.Refused, "a dependency on no visible mission is refused: " + reply);
                        AssertContains("dependsOnMissionId not found", reply.Body, surface + ": the refusal names the dependency: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNull(stored!.DependsOnMissionId, surface + ": nothing changes");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateMission_EmptyDependency_ClearsItOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission upstream = await SeedAsync(harness, surface + "-upstream").ConfigureAwait(false);
                        Mission mission = await SeedAsync(harness, surface).ConfigureAwait(false);
                        mission.DependsOnMissionId = upstream.Id;
                        await harness.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                        SurfaceReply reply = await UpdateAsync(harness, surface, mission.Id,
                            new Dictionary<string, object?> { { "DependsOnMissionId", "" } }).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "clearing a dependency is accepted: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNull(stored!.DependsOnMissionId, surface + ": an empty dependency clears it");
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> SeedAsync(SurfaceParityHarness harness, string label)
        {
            return await harness.Driver.Missions.CreateAsync(new Mission("title " + label)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                Description = "description " + label,
                Persona = "Worker",
                Priority = 100,
                Status = MissionStatusEnum.Pending
            }).ConfigureAwait(false);
        }

        private static Task<SurfaceReply> UpdateAsync(SurfaceParityHarness harness, string surface, string missionId, Dictionary<string, object?> fields)
        {
            switch (surface)
            {
                case "REST":
                    return harness.RestAsync(HttpMethod.Put, "/api/v1/missions/" + missionId, fields);
                case "WebSocket":
                    return harness.WebSocketAsync("update_mission", missionId, fields);
                default:
                    Dictionary<string, object?> args = new Dictionary<string, object?> { { "missionId", missionId } };
                    foreach (KeyValuePair<string, object?> field in fields)
                        args[Char.ToLowerInvariant(field.Key[0]) + field.Key.Substring(1)] = field.Value;
                    return harness.McpAsync("armada_update_mission", args);
            }
        }
    }
}
