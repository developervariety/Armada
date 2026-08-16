namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests the modelContext write guard on armada_update_vessel_context: captains (no operatorOverride)
    /// are blocked from mutating modelContext, while the orchestrator (operatorOverride=true) may write or
    /// clear it. projectContext remains writable without the override. Also covers the branchCleanupPolicy
    /// argument on armada_update_vessel, which must persist a declared policy and reject anything else.
    /// </summary>
    public sealed class McpVesselToolsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Vessel Tools";

        private Func<JsonElement?, Task<object>> CaptureContextHandler(TestDatabase testDb)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            McpVesselTools.Register(
                (name, _, _, h) => { if (name == "armada_update_vessel_context") handler = h; },
                testDb.Driver);
            AssertNotNull(handler, "armada_update_vessel_context handler must be registered");
            return handler!;
        }

        private Func<JsonElement?, Task<object>> CaptureUpdateHandler(TestDatabase testDb)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            McpVesselTools.Register(
                (name, _, _, h) => { if (name == "armada_update_vessel") handler = h; },
                testDb.Driver);
            AssertNotNull(handler, "armada_update_vessel handler must be registered");
            return handler!;
        }

        private static async Task<Vessel> SeedVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("ctx-vessel", "https://github.com/test/repo.git");
            vessel.ModelContext = "ORIGINAL blob";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ModelContextWrite_WithoutOperatorOverride_IsBlocked", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureContextHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        modelContext = "captain tried to write this"
                    });
                    object result = await handler(args).ConfigureAwait(false);

                    AssertTrue(JsonSerializer.Serialize(result).Contains("blocked for captains"),
                        "a modelContext write without operatorOverride must be rejected");
                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual("ORIGINAL blob", after!.ModelContext, "the modelContext must be unchanged after a blocked write");
                }
            }).ConfigureAwait(false);

            await RunTest("ModelContextWrite_WithOperatorOverride_IsApplied", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureContextHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        modelContext = "REFRESHED durable-gotcha layer",
                        operatorOverride = true
                    });
                    object result = await handler(args).ConfigureAwait(false);

                    AssertFalse(JsonSerializer.Serialize(result).Contains("\"Error\""),
                        "an operator-override modelContext write must not error: " + JsonSerializer.Serialize(result));
                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual("REFRESHED durable-gotcha layer", after!.ModelContext,
                        "the modelContext must be written when operatorOverride=true");
                }
            }).ConfigureAwait(false);

            await RunTest("ProjectContextWrite_WithoutOverride_IsAllowed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureContextHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        projectContext = "lean architecture summary"
                    });
                    object result = await handler(args).ConfigureAwait(false);

                    AssertFalse(JsonSerializer.Serialize(result).Contains("\"Error\""),
                        "a projectContext-only update must not be blocked");
                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual("lean architecture summary", after!.ProjectContext, "projectContext must be written");
                    AssertEqual("ORIGINAL blob", after.ModelContext, "modelContext must be untouched by a projectContext-only update");
                }
            }).ConfigureAwait(false);

            await RunTest("BranchCleanupPolicy_DeclaredValue_IsPersisted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    AssertTrue(vessel.BranchCleanupPolicy == null, "the seeded vessel must start with no explicit policy");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        branchCleanupPolicy = "LocalAndRemote"
                    });
                    object result = await handler(args).ConfigureAwait(false);

                    AssertFalse(JsonSerializer.Serialize(result).Contains("\"Error\""),
                        "a declared branchCleanupPolicy must be accepted: " + JsonSerializer.Serialize(result));
                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertTrue(after!.BranchCleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote,
                        "reading the vessel back must show LocalAndRemote");

                    JsonElement other = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        projectContext = "an unrelated edit"
                    });
                    await handler(other).ConfigureAwait(false);
                    Vessel? afterOther = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertTrue(afterOther!.BranchCleanupPolicy == BranchCleanupPolicyEnum.LocalAndRemote,
                        "an update that omits branchCleanupPolicy must leave the stored policy unchanged");
                }
            }).ConfigureAwait(false);

            await RunTest("BranchCleanupPolicy_UnknownValue_IsRejected", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        branchCleanupPolicy = "RemoteOnly"
                    });
                    object result = await handler(args).ConfigureAwait(false);

                    AssertTrue(JsonSerializer.Serialize(result).Contains("branchCleanupPolicy must be one of"),
                        "an unrecognized policy must be rejected with a clear error, not silently defaulted");
                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertTrue(after!.BranchCleanupPolicy == null,
                        "a rejected policy must leave the stored value untouched");
                }
            }).ConfigureAwait(false);

            await RunTest("BranchCleanupPolicy_NumericOrdinal_IsRejected", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        branchCleanupPolicy = "1"
                    });
                    object result = await handler(args).ConfigureAwait(false);

                    AssertTrue(JsonSerializer.Serialize(result).Contains("branchCleanupPolicy must be one of"),
                        "an ordinal must not be accepted as a policy name");
                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertTrue(after!.BranchCleanupPolicy == null,
                        "a rejected ordinal must leave the stored value untouched");
                }
            }).ConfigureAwait(false);
        }
    }
}
