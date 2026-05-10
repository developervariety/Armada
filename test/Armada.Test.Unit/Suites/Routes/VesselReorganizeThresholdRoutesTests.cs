namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the reorganizeThreshold field exposed via McpVesselTools.
    /// Covers: DB round-trip, MCP set/clear, no-clobber on absent, zero rejection, negative rejection, non-integer rejection.
    /// </summary>
    public class VesselReorganizeThresholdRoutesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel ReorganizeThreshold Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ReorganizeThreshold_SetViaMcp_RoundTripsThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-set-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rorg-set-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reorganizeThreshold = 50
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("\"ReorganizeThreshold\":50", resultJson, "ReorganizeThreshold should be present in add result");

                    Vessel? read = await testDb.Driver.Vessels.ReadAsync(
                        JsonSerializer.Deserialize<Vessel>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Vessel should exist after add");
                    AssertTrue(read!.ReorganizeThreshold.HasValue, "ReorganizeThreshold should be persisted");
                    AssertEqual(50, read.ReorganizeThreshold!.Value, "ReorganizeThreshold should match the value set");
                }
            });

            await RunTest("ReorganizeThreshold_ClearedToNull_McpUpdateVessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-clear-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rorg-clear-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.ReorganizeThreshold = 40;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        reorganizeThreshold = (int?)null
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertFalse(updated!.ReorganizeThreshold.HasValue, "ReorganizeThreshold should be cleared to null");
                }
            });

            await RunTest("ReorganizeThreshold_UpdatedToPositive_McpUpdateVessel_SetsNewValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-update-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rorg-update-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.ReorganizeThreshold = 20;
                    vessel.ReflectionThreshold = 9;
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
                        reorganizeThreshold = 45
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertEqual(45, updated!.ReorganizeThreshold!.Value, "ReorganizeThreshold should be updated");
                    AssertEqual(9, updated.ReflectionThreshold!.Value, "ReflectionThreshold should remain unchanged");
                    AssertEqual("msn_existing_reflection", updated.LastReflectionMissionId, "LastReflectionMissionId should remain unchanged");
                }
            });

            await RunTest("AbsentReorganizeThreshold_McpUpdateVessel_DoesNotClobberExistingValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-noclobber-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rorg-noclobber-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.ReorganizeThreshold = 60;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    // Update name only -- reorganizeThreshold is absent
                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        name = "rorg-noclobber-vessel-renamed"
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertTrue(updated!.ReorganizeThreshold.HasValue, "ReorganizeThreshold must not be clobbered");
                    AssertEqual(60, updated.ReorganizeThreshold!.Value, "ReorganizeThreshold value must be unchanged");
                }
            });

            await RunTest("ReorganizeThreshold_McpSchemas_AdvertiseIntegerProperty", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    object? addSchema = null;
                    object? updateSchema = null;

                    McpVesselTools.Register(
                        (name, _, schema, _) =>
                        {
                            if (name == "armada_add_vessel")
                                addSchema = schema;
                            if (name == "armada_update_vessel")
                                updateSchema = schema;
                        },
                        testDb.Driver);

                    AssertNotNull(addSchema, "armada_add_vessel schema must be registered");
                    AssertNotNull(updateSchema, "armada_update_vessel schema must be registered");

                    string addSchemaJson = JsonSerializer.Serialize(addSchema);
                    string updateSchemaJson = JsonSerializer.Serialize(updateSchema);
                    AssertContains("reorganizeThreshold", addSchemaJson, "Add schema should advertise reorganizeThreshold");
                    AssertContains("\"type\":\"integer\"", addSchemaJson, "Add schema should type reorganizeThreshold as integer");
                    AssertContains("reorganizeThreshold", updateSchemaJson, "Update schema should advertise reorganizeThreshold");
                    AssertContains("\"type\":\"integer\"", updateSchemaJson, "Update schema should type reorganizeThreshold as integer");
                }
            });

            await RunTest("ZeroReorganizeThreshold_McpAddVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-zero-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rorg-zero-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reorganizeThreshold = 0
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("positive", resultJson, "Error response must mention positive integer requirement");
                }
            });

            await RunTest("NegativeReorganizeThreshold_McpAddVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-neg-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rorg-neg-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reorganizeThreshold = -1
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("positive", resultJson, "Error response must mention positive integer requirement");
                }
            });

            await RunTest("NonIntegerReorganizeThreshold_McpAddVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-string-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "rorg-string-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        reorganizeThreshold = "50"
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("integer", resultJson, "Error response must mention integer requirement");
                }
            });

            await RunTest("ZeroReorganizeThreshold_McpUpdateVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-zero-upd-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rorg-zero-upd-vessel", "https://github.com/test/repo.git");
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
                        reorganizeThreshold = 0
                    });
                    object result = await updateHandler!(updateArgs).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("positive", resultJson, "Error response must mention positive integer requirement");
                }
            });

            await RunTest("NegativeReorganizeThreshold_McpUpdateVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-neg-upd-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rorg-neg-upd-vessel", "https://github.com/test/repo.git");
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
                        reorganizeThreshold = -5
                    });
                    object result = await updateHandler!(updateArgs).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("positive", resultJson, "Error response must mention positive integer requirement");
                }
            });

            await RunTest("NonIntegerReorganizeThreshold_McpUpdateVessel_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rorg-string-upd-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rorg-string-upd-vessel", "https://github.com/test/repo.git");
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
                        reorganizeThreshold = "5"
                    });
                    object result = await updateHandler!(updateArgs).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("integer", resultJson, "Error response must mention integer requirement");
                }
            });
        }
    }
}
