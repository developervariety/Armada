namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the vessel database methods: create, read, read-by-name, fleet-filtered
    /// enumeration, update, delete, and existence, plus persistence of the project context, style
    /// guide, and GitHub token override fields (including null and cleared values). Includes a
    /// negative case for reading a missing vessel. Each case runs against its own fresh SQLite store.
    /// </summary>
    public sealed class VesselDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.VesselDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Vessel Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_vessel", "CreateAsync returns vessel", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("TestVessel", "https://github.com/test/repo");
                    Vessel result = await db.Vessels.CreateAsync(vessel);

                    AssertNotNull(result);
                    AssertEqual("TestVessel", result.Name);
                }
            }));

            cases.Add(CaseAsync("read_async_returns_created_vessel", "ReadAsync returns created vessel", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("ReadTest", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(result);
                    AssertEqual("ReadTest", result!.Name);
                    AssertEqual("main", result.DefaultBranch);
                }
            }));

            cases.Add(CaseAsync("read_by_name_async_returns_correct_vessel", "ReadByNameAsync returns correct vessel", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("NameLookup", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadByNameAsync("NameLookup");
                    AssertNotNull(result);
                    AssertEqual(vessel.Id, result!.Id);
                }
            }));

            cases.Add(CaseAsync("enumerate_by_fleet_async_filters_correctly", "EnumerateByFleetAsync filters correctly", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("TestFleet");
                    await db.Fleets.CreateAsync(fleet);

                    Vessel v1 = new Vessel("InFleet", "https://github.com/test/repo1");
                    v1.FleetId = fleet.Id;
                    Vessel v2 = new Vessel("NoFleet", "https://github.com/test/repo2");

                    await db.Vessels.CreateAsync(v1);
                    await db.Vessels.CreateAsync(v2);

                    List<Vessel> fleetVessels = await db.Vessels.EnumerateByFleetAsync(fleet.Id);
                    AssertEqual(1, fleetVessels.Count);
                    AssertEqual("InFleet", fleetVessels[0].Name);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_vessel", "UpdateAsync modifies vessel", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("Original", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    vessel.Name = "Updated";
                    vessel.DefaultBranch = "develop";
                    vessel.Active = false;
                    await db.Vessels.UpdateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertEqual("Updated", result!.Name);
                    AssertEqual("develop", result.DefaultBranch);
                    AssertFalse(result.Active);
                }
            }));

            cases.Add(CaseAsync("delete_async_removes_vessel", "DeleteAsync removes vessel", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("ToDelete", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    await db.Vessels.DeleteAsync(vessel.Id);
                    AssertNull(await db.Vessels.ReadAsync(vessel.Id));
                }
            }));

            cases.Add(CaseAsync("exists_async_works_correctly", "ExistsAsync works correctly", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("ExistsTest", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    AssertTrue(await db.Vessels.ExistsAsync(vessel.Id));
                    AssertFalse(await db.Vessels.ExistsAsync("vsl_nonexistent"));
                }
            }));

            cases.Add(CaseAsync("create_async_with_project_context_and_style_guide_persists_both_fields", "CreateAsync with ProjectContext and StyleGuide persists both fields", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("ContextVessel", "https://github.com/test/repo");
                    vessel.ProjectContext = "This is a .NET 8 web API with PostgreSQL backend.";
                    vessel.StyleGuide = "Use PascalCase for public members, camelCase for private.";
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(result);
                    AssertEqual("This is a .NET 8 web API with PostgreSQL backend.", result!.ProjectContext);
                    AssertEqual("Use PascalCase for public members, camelCase for private.", result.StyleGuide);
                }
            }));

            cases.Add(CaseAsync("create_async_with_null_project_context_and_style_guide_persists_nulls", "CreateAsync with null ProjectContext and StyleGuide persists nulls", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("NullContextVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(result);
                    AssertNull(result!.ProjectContext);
                    AssertNull(result.StyleGuide);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_project_context_and_style_guide", "UpdateAsync modifies ProjectContext and StyleGuide", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("UpdateContextVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    vessel.ProjectContext = "Updated project context";
                    vessel.StyleGuide = "Updated style guide";
                    await db.Vessels.UpdateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertEqual("Updated project context", result!.ProjectContext);
                    AssertEqual("Updated style guide", result.StyleGuide);
                }
            }));

            cases.Add(CaseAsync("update_async_can_set_project_context_and_style_guide_to_null", "UpdateAsync can set ProjectContext and StyleGuide to null", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("ClearContextVessel", "https://github.com/test/repo");
                    vessel.ProjectContext = "Initial context";
                    vessel.StyleGuide = "Initial style";
                    await db.Vessels.CreateAsync(vessel);

                    vessel.ProjectContext = null;
                    vessel.StyleGuide = null;
                    await db.Vessels.UpdateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNull(result!.ProjectContext);
                    AssertNull(result.StyleGuide);
                }
            }));

            cases.Add(CaseAsync("read_by_name_async_returns_project_context_and_style_guide", "ReadByNameAsync returns ProjectContext and StyleGuide", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("NameLookupContext", "https://github.com/test/repo");
                    vessel.ProjectContext = "Context via name lookup";
                    vessel.StyleGuide = "Style via name lookup";
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadByNameAsync("NameLookupContext");
                    AssertNotNull(result);
                    AssertEqual("Context via name lookup", result!.ProjectContext);
                    AssertEqual("Style via name lookup", result.StyleGuide);
                }
            }));

            cases.Add(CaseAsync("enumerate_by_fleet_async_returns_project_context_and_style_guide", "EnumerateByFleetAsync returns ProjectContext and StyleGuide", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Fleet fleet = new Fleet("ContextFleet");
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("FleetContextVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    vessel.ProjectContext = "Fleet vessel context";
                    vessel.StyleGuide = "Fleet vessel style";
                    await db.Vessels.CreateAsync(vessel);

                    List<Vessel> results = await db.Vessels.EnumerateByFleetAsync(fleet.Id);
                    AssertEqual(1, results.Count);
                    AssertEqual("Fleet vessel context", results[0].ProjectContext);
                    AssertEqual("Fleet vessel style", results[0].StyleGuide);
                }
            }));

            cases.Add(CaseAsync("create_and_update_async_persists_github_token_override", "CreateAndUpdateAsync persists GitHubTokenOverride", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("Token Vessel", "https://github.com/test/repo");
                    vessel.GitHubTokenOverride = "  ghp_repo_token  ";
                    vessel.NormalizeGitHubTokenOverride();
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? created = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(created);
                    AssertEqual("ghp_repo_token", created!.GitHubTokenOverride);
                    AssertTrue(created.HasGitHubTokenOverride);

                    created.GitHubTokenOverride = "   ";
                    created.NormalizeGitHubTokenOverride();
                    await db.Vessels.UpdateAsync(created);

                    Vessel? cleared = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(cleared);
                    AssertNull(cleared!.GitHubTokenOverride);
                    AssertFalse(cleared.HasGitHubTokenOverride);
                }
            }));

            // Audit addition: read of a missing vessel id must return null (not-found path).
            cases.Add(CaseAsync("read_async_missing_returns_null", "ReadAsync missing returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Vessel? result = await db.Vessels.ReadAsync("vsl_nonexistent");
                    AssertNull(result);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Vessel Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
