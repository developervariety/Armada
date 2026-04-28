namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the autoLandPredicate field exposed via VesselRoutes (REST) and McpVesselTools (MCP).
    /// Covers: DB round-trip, validation error on bad JSON, and partial-update no-clobber semantics.
    /// </summary>
    public class VesselAutoLandPredicateRoutesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel AutoLandPredicate Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ValidAutoLandPredicateJson_RoundTripsThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string predicateJson = "{\"enabled\":true,\"maxFiles\":5}";
                    Vessel vessel = new Vessel("alp-rt-vessel", "https://github.com/test/repo.git");
                    vessel.AutoLandPredicate = predicateJson;
                    Vessel created = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    AssertNotNull(created.AutoLandPredicate, "AutoLandPredicate should be persisted on create");

                    Vessel? read = await testDb.Driver.Vessels.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Vessel should be readable after create");
                    AssertNotNull(read!.AutoLandPredicate, "AutoLandPredicate should survive DB round-trip");
                    AssertEqual(predicateJson, read.AutoLandPredicate);
                }
            });

            await RunTest("InvalidAutoLandPredicateJson_McpAddVessel_ReturnsErrorMessage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("alp-err-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    // autoLandPredicate = 123 (JSON number) cannot be deserialized as AutoLandPredicate
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "alp-invalid-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        autoLandPredicate = 123
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("invalid autoLandPredicate JSON", resultJson, "Error response must contain validation message");
                }
            });

            await RunTest("AbsentAutoLandPredicate_McpUpdateVessel_DoesNotClobberExistingValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("alp-noclobber-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    string originalPredicate = "{\"enabled\":true,\"maxFiles\":10}";
                    Vessel vessel = new Vessel("alp-noclobber-vessel", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel.AutoLandPredicate = originalPredicate;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    // Update name only -- autoLandPredicate is absent from args
                    JsonElement updateArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        name = "alp-noclobber-vessel-renamed"
                    });
                    await updateHandler!(updateArgs).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertEqual(
                        originalPredicate,
                        updated!.AutoLandPredicate,
                        "AutoLandPredicate must not be clobbered by a partial update that omits it");
                }
            });
        }
    }
}
