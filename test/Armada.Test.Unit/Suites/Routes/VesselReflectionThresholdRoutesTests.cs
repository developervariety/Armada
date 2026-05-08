namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the reflectionThreshold field exposed via McpVesselTools and ArmadaSettings defaults.
    /// Covers: DB round-trip, MCP set/clear, no-clobber on absent, validation, settings defaults, and internal reflection state guards.
    /// </summary>
    public class VesselReflectionThresholdRoutesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel ReflectionThreshold Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ReflectionThreshold_SetViaMcp_RoundTripsThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-set-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rt-set-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reflectionThreshold = 25
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("\"ReflectionThreshold\":25", resultJson, "ReflectionThreshold should be present in add result");

                    Vessel? read = await testDb.Driver.Vessels.ReadAsync(
                        JsonSerializer.Deserialize<Vessel>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Vessel should exist after add");
                    AssertTrue(read!.ReflectionThreshold.HasValue, "ReflectionThreshold should be persisted");
                    AssertEqual(25, read.ReflectionThreshold!.Value, "ReflectionThreshold should match the value set");
                }
            });

            await RunTest("ReflectionThreshold_ClearedToNull_McpUpdateVessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-clear-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rt-clear-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.ReflectionThreshold = 20;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        reflectionThreshold = (int?)null
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertFalse(updated!.ReflectionThreshold.HasValue, "ReflectionThreshold should be cleared to null");
                }
            });

            await RunTest("AbsentReflectionThreshold_McpUpdateVessel_DoesNotClobberExistingValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-noclobber-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rt-noclobber-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.ReflectionThreshold = 30;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    // Update name only -- reflectionThreshold is absent
                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        name = "rt-noclobber-vessel-renamed"
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertTrue(updated!.ReflectionThreshold.HasValue, "ReflectionThreshold must not be clobbered");
                    AssertEqual(30, updated.ReflectionThreshold!.Value, "ReflectionThreshold value must be unchanged");
                }
            });

            await RunTest("NegativeReflectionThreshold_McpAddVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-neg-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rt-neg-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reflectionThreshold = -1
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("negative", resultJson, "Error response must mention negative threshold");
                }
            });

            await RunTest("NonIntegerReflectionThreshold_McpAddVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-string-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rt-string-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reflectionThreshold = "25"
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("integer", resultJson, "Error response must mention integer threshold");
                }
            });

            await RunTest("NegativeReflectionThreshold_McpUpdateVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-neg-upd-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rt-neg-upd-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        reflectionThreshold = -5
                    });
                    object result = await updateHandler!(updateArgs).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("negative", resultJson, "Error response must mention negative threshold");
                }
            });

            await RunTest("NonIntegerReflectionThreshold_McpUpdateVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-string-upd-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rt-string-upd-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        reflectionThreshold = "5"
                    });
                    object result = await updateHandler!(updateArgs).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("integer", resultJson, "Error response must mention integer threshold");
                }
            });

            await RunTest("LastReflectionMissionId_McpAddVessel_IgnoresInjectedValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-internal-create-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rt-internal-create-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        lastReflectionMissionId = "msn_client_supplied"
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    Vessel? read = await testDb.Driver.Vessels.ReadAsync(
                        JsonSerializer.Deserialize<Vessel>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Vessel should exist after add");
                    AssertNull(read!.LastReflectionMissionId, "LastReflectionMissionId must not be client-writeable on create");
                }
            });

            await RunTest("LastReflectionMissionId_McpUpdateVessel_PreservesExistingValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rt-internal-update-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rt-internal-update-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.LastReflectionMissionId = "msn_existing_reflection";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        name = "rt-internal-update-renamed",
                        lastReflectionMissionId = "msn_client_override"
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertEqual("msn_existing_reflection", updated!.LastReflectionMissionId, "LastReflectionMissionId must be preserved on update");
                }
            });

            await RunTest("ArmadaSettings_ReflectionDefaults_AreStable", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(15, settings.DefaultReflectionThreshold, "DefaultReflectionThreshold default must be 15");
                AssertEqual(100, settings.InitialReflectionWindow, "InitialReflectionWindow default must be 100");
                AssertEqual(400000, settings.DefaultReflectionTokenBudget, "DefaultReflectionTokenBudget default must be 400000");
                return Task.CompletedTask;
            });

            await RunTest("ArmadaSettings_InvalidReflectionValues_Throw", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentOutOfRangeException>(() => settings.DefaultReflectionThreshold = 0, "DefaultReflectionThreshold must reject zero");
                AssertThrows<ArgumentOutOfRangeException>(() => settings.InitialReflectionWindow = 0, "InitialReflectionWindow must reject zero");
                AssertThrows<ArgumentOutOfRangeException>(() => settings.DefaultReflectionTokenBudget = 0, "DefaultReflectionTokenBudget must reject zero");
                return Task.CompletedTask;
            });
        }
    }
}
