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
            return McpTestCaller.Wrap(handler!);
        }

        private Func<JsonElement?, Task<object>> CaptureUpdateHandler(TestDatabase testDb)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            McpVesselTools.Register(
                (name, _, _, h) => { if (name == "armada_update_vessel") handler = McpTestCaller.Wrap(h); },
                testDb.Driver);
            AssertNotNull(handler, "armada_update_vessel handler must be registered");
            return handler!;
        }

        private static readonly JsonSerializerOptions _TransportJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private Func<JsonElement?, Task<object>> CaptureRawHandler(TestDatabase testDb, string toolName)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            McpVesselTools.Register(
                (name, _, _, h) => { if (name == toolName) handler = h; },
                testDb.Driver);
            AssertNotNull(handler, toolName + " handler must be registered");
            return handler!;
        }

        private static async Task<object> CallAsAsync(Func<JsonElement?, Task<object>> handler, AuthContext caller, object args)
        {
            using (McpCallerContext.Begin(caller))
            {
                return await handler(JsonSerializer.SerializeToElement(args)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Assert a tool result carries neither the token value nor a gitHubTokenOverride key, in either the
        /// transport's camelCase form or the default form.
        /// </summary>
        private void AssertTokenNotEchoed(object result, string token)
        {
            foreach (string text in new[] { JsonSerializer.Serialize(result, _TransportJsonOptions), JsonSerializer.Serialize(result) })
            {
                AssertFalse(text.Contains(token, StringComparison.Ordinal), "a tool result must never carry the token value: " + text);
                AssertFalse(text.Contains("\"gitHubTokenOverride\"", StringComparison.OrdinalIgnoreCase), "a tool result must never carry a gitHubTokenOverride key: " + text);
            }
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
            await RunTest("AddVessel_EnableModelContext_SchemaDefaultMatchesCreatedVessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    object? schema = null;
                    McpVesselTools.Register(
                        (name, _, s, h) => { if (name == "armada_add_vessel") { handler = h; schema = s; } },
                        testDb.Driver);
                    AssertNotNull(handler, "armada_add_vessel handler must be registered");

                    ToolSchemaDocument? document = JsonSerializer.Deserialize<ToolSchemaDocument>(JsonSerializer.Serialize(schema), _TransportJsonOptions);
                    ToolSchemaProperty? property = null;
                    document?.Properties?.TryGetValue("enableModelContext", out property);
                    AssertContains("(default true)", property?.Description ?? "", "the enableModelContext schema names the default the handler applies");

                    Fleet fleet = await testDb.Driver.Fleets.CreateAsync(new Fleet("default-context-fleet")).ConfigureAwait(false);
                    object result = await CallAsAsync(handler!, McpTestCaller.Operator, new
                    {
                        name = "default-context-vessel",
                        repoUrl = "https://github.com/test/default-context.git",
                        fleetId = fleet.Id
                    }).ConfigureAwait(false);
                    Vessel created = (Vessel)result;
                    AssertTrue(created.EnableModelContext, "a vessel added without enableModelContext has model context enabled");
                }
            }).ConfigureAwait(false);

            await RunTest("AddVessel_GitHubTokenOverride_IsStoredTrimmedOwnedByCallerAndNeverEchoed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> add = CaptureRawHandler(testDb, "armada_add_vessel");
                    Fleet fleet = await testDb.Driver.Fleets.CreateAsync(new Fleet("token-add-fleet")).ConfigureAwait(false);
                    string token = "ghp_unit_add_" + Guid.NewGuid().ToString("N");
                    object result = await CallAsAsync(add, McpTestCaller.Operator, new
                    {
                        name = "token-add-vessel",
                        repoUrl = "https://github.com/test/token-add.git",
                        fleetId = fleet.Id,
                        gitHubTokenOverride = "  " + token + "  "
                    }).ConfigureAwait(false);

                    AssertTokenNotEchoed(result, token);
                    Vessel created = (Vessel)result;
                    AssertTrue(created.HasGitHubTokenOverride, "the result reports that an override is configured");
                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(token, stored!.GitHubTokenOverride, "the supplied override is stored, trimmed");
                    AssertEqual(McpTestCaller.Operator.TenantId, stored.TenantId, "the vessel belongs to the caller's tenant");
                    AssertEqual(McpTestCaller.Operator.UserId, stored.UserId, "the vessel belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_OmittedGitHubTokenOverride_KeepsStoredValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> update = CaptureRawHandler(testDb, "armada_update_vessel");
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.GitHubTokenOverride = "ghp_unit_keep_stored";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    object result = await CallAsAsync(update, McpTestCaller.Operator, new { vesselId = vessel.Id, name = "renamed" }).ConfigureAwait(false);

                    AssertTokenNotEchoed(result, "ghp_unit_keep_stored");
                    AssertTrue(((Vessel)result).HasGitHubTokenOverride, "the result still reports the stored override");
                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual("renamed", stored!.Name, "the other field was applied");
                    AssertEqual("ghp_unit_keep_stored", stored.GitHubTokenOverride, "an update that omits the override keeps the stored value");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_EmptyGitHubTokenOverride_ClearsStoredValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> update = CaptureRawHandler(testDb, "armada_update_vessel");
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.GitHubTokenOverride = "ghp_unit_clear_me";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    object result = await CallAsAsync(update, McpTestCaller.Operator, new { vesselId = vessel.Id, gitHubTokenOverride = "" }).ConfigureAwait(false);

                    AssertTokenNotEchoed(result, "ghp_unit_clear_me");
                    AssertFalse(((Vessel)result).HasGitHubTokenOverride, "the result reports no override");
                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(stored!.GitHubTokenOverride, "an explicit empty override clears the stored value");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_EmptyGitHubTokenOverride_SurvivesTransportNormalizationAndClears", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>>? handler = null;
                    object? schema = null;
                    McpVesselTools.Register(
                        (name, _, s, h) => { if (name == "armada_update_vessel") { handler = h; schema = s; } },
                        testDb.Driver);
                    AssertNotNull(handler, "armada_update_vessel handler must be registered");
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.GitHubTokenOverride = "ghp_unit_transport_clear";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    // The transport normalizes arguments against the registered schema before the handler runs.
                    JsonElement raw = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, gitHubTokenOverride = "" });
                    JsonElement? normalized = McpToolArgumentNormalizer.Normalize(raw, schema, _TransportJsonOptions);
                    AssertTrue(normalized!.Value.TryGetProperty("gitHubTokenOverride", out _), "the empty override reaches the handler");
                    using (McpCallerContext.Begin(McpTestCaller.Operator))
                    {
                        await handler!(normalized).ConfigureAwait(false);
                    }

                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(stored!.GitHubTokenOverride, "an empty override sent through the transport clears the stored value");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_NewGitHubTokenOverride_ReplacesStoredValueWithoutEcho", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> update = CaptureRawHandler(testDb, "armada_update_vessel");
                    Vessel vessel = await SeedVesselAsync(testDb).ConfigureAwait(false);
                    vessel.GitHubTokenOverride = "ghp_unit_old_value";
                    await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                    string token = "ghp_unit_new_" + Guid.NewGuid().ToString("N");

                    object result = await CallAsAsync(update, McpTestCaller.Operator, new { vesselId = vessel.Id, gitHubTokenOverride = token }).ConfigureAwait(false);

                    AssertTokenNotEchoed(result, token);
                    AssertTokenNotEchoed(result, "ghp_unit_old_value");
                    Vessel? stored = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(token, stored!.GitHubTokenOverride, "a supplied override replaces the stored value");
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateVessel_CallerWhoCannotEditVessel_CannotSetGitHubTokenOverride", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata ownerTenant = new TenantMetadata("Vessel Owner Tenant");
                    await testDb.Driver.Tenants.CreateAsync(ownerTenant).ConfigureAwait(false);
                    UserMaster owner = new UserMaster(ownerTenant.Id, "owner@example.com", "pass");
                    await testDb.Driver.Users.CreateAsync(owner).ConfigureAwait(false);
                    Vessel vessel = new Vessel("owned-vessel", "https://github.com/test/owned.git")
                    {
                        TenantId = ownerTenant.Id,
                        UserId = owner.Id,
                        GitHubTokenOverride = "ghp_unit_owner_value"
                    };
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>> update = CaptureRawHandler(testDb, "armada_update_vessel");
                    AuthContext foreignTenantAdmin = AuthContext.Authenticated("ten_foreign", "usr_foreign", false, true, "Test", null, "Foreign tenant admin");
                    AuthContext sameTenantOtherUser = AuthContext.Authenticated(ownerTenant.Id, "usr_other", false, false, "Test", null, "Other user");

                    foreach (AuthContext caller in new[] { foreignTenantAdmin, sameTenantOtherUser })
                    {
                        object result = await CallAsAsync(update, caller, new { vesselId = vessel.Id, gitHubTokenOverride = "ghp_unit_intruder" }).ConfigureAwait(false);
                        AssertContains("Vessel not found", JsonSerializer.Serialize(result), caller.PrincipalDisplay + " is refused as if the vessel did not exist");
                        Vessel? stored = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                        AssertEqual("ghp_unit_owner_value", stored!.GitHubTokenOverride, caller.PrincipalDisplay + " cannot change the stored override");
                    }

                    AuthContext ownerCaller = AuthContext.Authenticated(ownerTenant.Id, owner.Id, false, false, "Test", null, "Owner");
                    object ownerResult = await CallAsAsync(update, ownerCaller, new { vesselId = vessel.Id, gitHubTokenOverride = "" }).ConfigureAwait(false);
                    AssertFalse(JsonSerializer.Serialize(ownerResult).Contains("\"Error\""), "the owner may update its vessel: " + JsonSerializer.Serialize(ownerResult));
                    Vessel? cleared = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(cleared!.GitHubTokenOverride, "the owner's explicit empty override clears it");
                }
            }).ConfigureAwait(false);

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

            await RunTest("GetVesselAndUpdateContext_CallerOutsideVesselOwner_IsRefusedWithoutChange", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("owned-vessel", "https://github.com/test/repo.git");
                    vessel.TenantId = Armada.Core.Constants.DefaultTenantId;
                    vessel.UserId = Armada.Core.Constants.DefaultUserId;
                    vessel.ProjectContext = "OWNER context";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? getHandler = null;
                    Func<JsonElement?, Task<object>>? contextHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_get_vessel") getHandler = h;
                            if (name == "armada_update_vessel_context") contextHandler = h;
                        },
                        testDb.Driver);
                    AssertNotNull(getHandler, "armada_get_vessel handler must be registered");
                    AssertNotNull(contextHandler, "armada_update_vessel_context handler must be registered");

                    JsonElement getArgs = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    JsonElement contextArgs = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, projectContext = "FOREIGN overwrite" });

                    List<AuthContext> foreignCallers = new List<AuthContext>
                    {
                        AuthContext.Authenticated("ten_other", "usr_other", false, false, "Test"),
                        AuthContext.Authenticated("ten_other", "usr_other_admin", false, true, "Test")
                    };
                    foreach (AuthContext foreign in foreignCallers)
                    {
                        string getJson;
                        string contextJson;
                        using (McpCallerContext.Begin(foreign))
                        {
                            getJson = JsonSerializer.Serialize(await getHandler!(getArgs).ConfigureAwait(false));
                            contextJson = JsonSerializer.Serialize(await contextHandler!(contextArgs).ConfigureAwait(false));
                        }
                        AssertContains("Vessel not found", getJson, "another tenant's caller cannot read the vessel");
                        AssertFalse(getJson.Contains("OWNER context", StringComparison.Ordinal), "another tenant's caller sees none of the vessel's content");
                        AssertContains("Vessel not found", contextJson, "another tenant's caller cannot update the vessel's context");
                    }

                    Vessel? after = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual("OWNER context", after!.ProjectContext, "a refused context update leaves the vessel unchanged");

                    AuthContext owner = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, false, false, "Test");
                    string ownerJson;
                    using (McpCallerContext.Begin(owner))
                    {
                        ownerJson = JsonSerializer.Serialize(await getHandler!(getArgs).ConfigureAwait(false));
                    }
                    AssertContains("OWNER context", ownerJson, "the owning user still reads the vessel");
                }
            }).ConfigureAwait(false);
        }

        private sealed class ToolSchemaDocument
        {
            public Dictionary<string, ToolSchemaProperty>? Properties { get; set; }
        }

        private sealed class ToolSchemaProperty
        {
            public string? Description { get; set; }
        }
    }
}
