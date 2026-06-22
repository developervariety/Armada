namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies that captain-accessible MCP paths block raw ModelContext mutation
    /// and return an actionable [CLAUDE.MD-PROPOSAL] message.
    /// </summary>
    public sealed class VesselModelContextBoundaryTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel ModelContext Boundary";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ArmadaUpdateVesselContext_NonEmptyModelContext_ReturnsBlockedError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("mc-fleet-1");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("mc-vessel-1", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpVesselTools.Register(
                        (name, _, _, h) => { if (name == "armada_update_vessel_context") handler = h; },
                        testDb.Driver);

                    AssertNotNull(handler, "armada_update_vessel_context handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        modelContext = "some accumulated context"
                    });

                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Error", resultJson, "Response must contain an Error field");
                    AssertContains("modelContext", resultJson, "Error message must reference modelContext");
                    AssertContains("CLAUDE.MD-PROPOSAL", resultJson, "Error message must direct to [CLAUDE.MD-PROPOSAL]");
                }
            });

            await RunTest("ArmadaUpdateVesselContext_EmptyModelContext_AllowsOtherFields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("mc-fleet-2");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("mc-vessel-2", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpVesselTools.Register(
                        (name, _, _, h) => { if (name == "armada_update_vessel_context") handler = h; },
                        testDb.Driver);

                    AssertNotNull(handler, "armada_update_vessel_context handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        projectContext = "new project context"
                    });

                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertDoesNotContain("\"Error\"", resultJson, "Omitting modelContext must not produce an error");
                    AssertContains("new project context", resultJson, "Updated projectContext must appear in response");
                }
            });

            await RunTest("ArmadaUpdateVessel_NonEmptyModelContext_ReturnsBlockedError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("mc-fleet-3");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("mc-vessel-3", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpVesselTools.Register(
                        (name, _, _, h) => { if (name == "armada_update_vessel") handler = h; },
                        testDb.Driver);

                    AssertNotNull(handler, "armada_update_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        modelContext = "raw context injection"
                    });

                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("Error", resultJson, "Response must contain an Error field");
                    AssertContains("modelContext", resultJson, "Error message must reference modelContext");
                    AssertContains("CLAUDE.MD-PROPOSAL", resultJson, "Error message must direct to [CLAUDE.MD-PROPOSAL]");
                }
            });

            await RunTest("ArmadaUpdateVessel_NoModelContext_OtherFieldsMutate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("mc-fleet-4");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("mc-vessel-4", "https://github.com/test/repo.git");
                    vessel.FleetId = fleet.Id;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpVesselTools.Register(
                        (name, _, _, h) => { if (name == "armada_update_vessel") handler = h; },
                        testDb.Driver);

                    AssertNotNull(handler, "armada_update_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        styleGuide = "updated style guide"
                    });

                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertDoesNotContain("\"Error\"", resultJson, "Omitting modelContext must not produce an error");
                    AssertContains("updated style guide", resultJson, "Updated styleGuide must appear in response");
                }
            });
        }

        private void AssertDoesNotContain(string unexpected, string actual, string message)
        {
            if (actual.Contains(unexpected, StringComparison.Ordinal))
                throw new Exception(message + " -- unexpected text found: " + unexpected);
        }
    }
}
