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
    /// Tests for the siblingRepos field exposed through McpVesselTools.
    /// </summary>
    public class VesselSiblingReposRoutesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel SiblingRepos Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Register_IncludesSiblingReposInAddAndUpdateSchemas", () =>
            {
                using (TestDatabase testDb = TestDatabaseHelper.CreateDatabaseAsync().GetAwaiter().GetResult())
                {
                    object? addSchema = null;
                    object? updateSchema = null;
                    McpVesselTools.Register(
                        (name, _, schema, _) =>
                        {
                            if (name == "armada_add_vessel") addSchema = schema;
                            if (name == "armada_update_vessel") updateSchema = schema;
                        },
                        testDb.Driver);

                    AssertNotNull(addSchema, "armada_add_vessel schema must be registered");
                    AssertNotNull(updateSchema, "armada_update_vessel schema must be registered");

                    string addJson = JsonSerializer.Serialize(addSchema);
                    string updateJson = JsonSerializer.Serialize(updateSchema);
                    AssertContains("siblingRepos", addJson, "Add schema must expose siblingRepos");
                    AssertContains("relativePath", addJson, "Add schema must describe relativePath");
                    AssertContains("siblingRepos", updateJson, "Update schema must expose siblingRepos");
                    AssertContains("MatchBranchElseDefault", updateJson, "Update schema must describe branch strategy");
                }
            });

            await RunTest("McpAddVessel_WithSiblingRepos_PersistsJsonArray", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("sr-add-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "sr-add-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        siblingRepos = new[]
                        {
                            new
                            {
                                repoUrl = "https://github.com/test/sib.git",
                                relativePath = "../Sibling",
                                branchStrategy = "DefaultOnly",
                                defaultBranch = "develop"
                            }
                        }
                    });

                    object result = await addHandler!(args).ConfigureAwait(false);
                    Vessel created = JsonSerializer.Deserialize<Vessel>(JsonSerializer.Serialize(result))!;
                    Vessel? persisted = await testDb.Driver.Vessels.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(persisted, "Created vessel must be readable");
                    AssertNotNull(persisted!.SiblingRepos, "SiblingRepos must be persisted by MCP add");
                    AssertEqual(1, persisted.GetSiblingRepos().Count);
                    AssertEqual("../Sibling", persisted.GetSiblingRepos()[0].RelativePath);
                    AssertEqual("develop", persisted.GetSiblingRepos()[0].DefaultBranch);
                }
            });

            await RunTest("McpUpdateVessel_AbsentSiblingRepos_DoesNotClobberExistingValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string original = "[{\"relativePath\":\"../Sibling\",\"repoUrl\":\"https://github.com/test/sib.git\"}]";
                    Vessel vessel = new Vessel("sr-noclobber-vessel", "https://github.com/test/repo.git");
                    vessel.SiblingRepos = original;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        name = "sr-noclobber-renamed"
                    });
                    await updateHandler!(args).ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel must exist after update");
                    AssertEqual(original, updated!.SiblingRepos, "Omitting siblingRepos must leave the field unchanged");
                }
            });

            await RunTest("McpUpdateVessel_NullOrEmptySiblingRepos_ClearsExistingValue", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("sr-clear-vessel", "https://github.com/test/repo.git");
                    vessel.SiblingRepos = "[{\"relativePath\":\"../Sibling\"}]";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? updateHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_update_vessel") updateHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(updateHandler, "armada_update_vessel handler must be registered");

                    JsonElement emptyArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        siblingRepos = Array.Empty<object>()
                    });
                    await updateHandler!(emptyArgs).ConfigureAwait(false);

                    Vessel? afterEmpty = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(afterEmpty, "Vessel must exist after empty-array clear");
                    AssertNull(afterEmpty!.SiblingRepos, "Empty siblingRepos array must clear the field");

                    afterEmpty.SiblingRepos = "[{\"relativePath\":\"../Sibling\"}]";
                    await testDb.Driver.Vessels.UpdateAsync(afterEmpty).ConfigureAwait(false);

                    JsonElement nullArgs = JsonSerializer.SerializeToElement(new
                    {
                        vesselId = vessel.Id,
                        siblingRepos = (object?)null
                    });
                    await updateHandler!(nullArgs).ConfigureAwait(false);

                    Vessel? afterNull = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(afterNull, "Vessel must exist after null clear");
                    AssertNull(afterNull!.SiblingRepos, "Null siblingRepos must clear the field");
                }
            });

            await RunTest("McpAddVessel_InvalidSiblingReposJson_ReturnsErrorMessage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("sr-invalid-fleet");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? addHandler = null;
                    McpVesselTools.Register(
                        (name, _, _, handler) => { if (name == "armada_add_vessel") addHandler = handler; },
                        testDb.Driver);

                    AssertNotNull(addHandler, "armada_add_vessel handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        name = "sr-invalid-vessel",
                        repoUrl = "https://github.com/test/repo.git",
                        fleetId = fleet.Id,
                        siblingRepos = 123
                    });
                    object result = await addHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("invalid siblingRepos JSON", resultJson, "Invalid siblingRepos input must return an error");
                }
            });
        }
    }
}
