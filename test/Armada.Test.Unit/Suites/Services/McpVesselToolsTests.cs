namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// argument on armada_update_vessel, which must persist a declared policy and reject anything else,
    /// and the preserve-on-omit merge for structured sub-objects: a caller who edits siblingRepos or
    /// defaultPlaybooks through the documented schema must not silently destroy a field the schema
    /// did not name, while an explicit empty value must still clear it deliberately.
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

            await RunTest("SiblingRepos_UpdateOmittingArtifactPaths_PreservesThem", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.SiblingRepos = JsonSerializer.Serialize(new[]
                    {
                        new { vesselRef = "sib-vessel", relativePath = "../ExampleSibling", extractionArtifactPaths = new[] { "output/extracted-artifacts", "output/decompiled-src" } }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    // Exactly what an operator following the documented schema used to send: the
                    // whole entry, with no mention of the artifact paths they never saw.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        siblingRepos = new[]
                        {
                            new { vesselRef = "sib-vessel", relativePath = "../ExampleSibling", defaultBranch = "main" }
                        }
                    });
                    await handler(args).ConfigureAwait(false);

                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    List<SiblingRepo>? siblings = JsonSerializer.Deserialize<List<SiblingRepo>>(
                        after!.SiblingRepos!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    AssertNotNull(siblings, "the sibling list must survive the update");
                    AssertEqual(1, siblings!.Count, "the update must leave exactly one sibling");
                    AssertNotNull(siblings[0].ExtractionArtifactPaths,
                        "an omitted extractionArtifactPaths must not erase the stored paths");
                    AssertEqual(2, siblings[0].ExtractionArtifactPaths!.Count,
                        "both stored artifact paths must survive an update that never mentioned them");
                    AssertEqual("output/extracted-artifacts", siblings[0].ExtractionArtifactPaths![0], "first artifact path must survive");
                    AssertEqual("output/decompiled-src", siblings[0].ExtractionArtifactPaths![1], "second artifact path must survive");
                    AssertEqual("main", siblings[0].DefaultBranch, "the field the caller did supply must be applied");
                }
            }).ConfigureAwait(false);

            await RunTest("SiblingRepos_UpdateWithEmptyArtifactPaths_ClearsThem", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.SiblingRepos = JsonSerializer.Serialize(new[]
                    {
                        new { vesselRef = "sib-vessel", relativePath = "../ExampleSibling", extractionArtifactPaths = new[] { "output/extracted-artifacts" } }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    // Preserve-on-omit must not make the field unclearable: an EXPLICIT empty
                    // array is the deliberate way to remove it.
                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        siblingRepos = new[]
                        {
                            new { vesselRef = "sib-vessel", relativePath = "../ExampleSibling", extractionArtifactPaths = new string[0] }
                        }
                    });
                    await handler(args).ConfigureAwait(false);

                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    List<SiblingRepo>? siblings = JsonSerializer.Deserialize<List<SiblingRepo>>(
                        after!.SiblingRepos!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    AssertNotNull(siblings, "the sibling list must survive the update");
                    AssertTrue(siblings![0].ExtractionArtifactPaths == null || siblings[0].ExtractionArtifactPaths!.Count == 0,
                        "an explicit empty array must clear the stored artifact paths");
                }
            }).ConfigureAwait(false);

            await RunTest("SiblingRepos_ArtifactPathsRoundTripWhenSupplied", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        siblingRepos = new[]
                        {
                            new { vesselRef = "sib-vessel", relativePath = "../ExampleSibling", extractionArtifactPaths = new[] { "output/extracted-artifacts" } }
                        }
                    });
                    await handler(args).ConfigureAwait(false);

                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    List<SiblingRepo>? siblings = JsonSerializer.Deserialize<List<SiblingRepo>>(
                        after!.SiblingRepos!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    AssertNotNull(siblings![0].ExtractionArtifactPaths, "a supplied artifact path must round-trip through the tool");
                    AssertEqual("output/extracted-artifacts", siblings[0].ExtractionArtifactPaths![0], "the supplied path must be stored verbatim");
                }
            }).ConfigureAwait(false);

            await RunTest("DefaultPlaybooks_UpdateOmittingInlineContent_PreservesIt", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureUpdateHandler(testDb);
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(new[]
                    {
                        new { playbookId = "pbk_example", deliveryMode = "InlineFullContent", inlineFullContent = "STORED BODY" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        defaultPlaybooks = new[]
                        {
                            new { playbookId = "pbk_example", deliveryMode = "InstructionWithReference" }
                        }
                    });
                    await handler(args).ConfigureAwait(false);

                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    List<SelectedPlaybook>? playbooks = JsonSerializer.Deserialize<List<SelectedPlaybook>>(
                        after!.DefaultPlaybooks!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    AssertEqual("STORED BODY", playbooks![0].InlineFullContent,
                        "an omitted inlineFullContent must not erase the stored body");
                    AssertTrue(playbooks[0].DeliveryMode == PlaybookDeliveryModeEnum.InstructionWithReference,
                        "the field the caller did supply must be applied");
                }
            }).ConfigureAwait(false);
        }
    }
}
