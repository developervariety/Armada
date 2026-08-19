namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Pins the write half of the per-persona captain override feature. The resolver, the model, and the
    /// voyage column all existed while nothing ever assigned CaptainOverridesJson, so every dispatch stored
    /// null and the resolver read an empty list forever: the feature was reachable in code and unreachable
    /// in practice. These tests hold the serializer, the round trip through the voyage row, and the dispatch
    /// seam that populates it.
    /// </summary>
    public sealed class CaptainOverridePersistenceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Override Persistence";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Overrides survive a serialize and deserialize round trip", () =>
            {
                List<CaptainAssignmentOverride> overrides = new List<CaptainAssignmentOverride>
                {
                    new CaptainAssignmentOverride("Worker", "cpt_exampleworker", CaptainTierEnum.Standard),
                    new CaptainAssignmentOverride("Judge", "cpt_examplejudge", CaptainTierEnum.Premium)
                };

                string? json = MissionService.SerializeCaptainOverrides(overrides);
                AssertNotNull(json, "Populated overrides should serialize");

                List<CaptainAssignmentOverride> parsed = MissionService.DeserializeCaptainOverrides(json);
                AssertEqual(2, parsed.Count, "Both overrides should survive the round trip");
                AssertEqual("Worker", parsed[0].Persona, "First persona");
                AssertEqual("cpt_exampleworker", parsed[0].CaptainId, "First captain");
                AssertEqual(CaptainTierEnum.Standard, parsed[0].FallbackTier, "First fallback tier");
                AssertEqual("Judge", parsed[1].Persona, "Second persona");
                AssertEqual(CaptainTierEnum.Premium, parsed[1].FallbackTier, "Second fallback tier");
            });

            await RunTest("Nothing to store leaves the column null rather than an empty array", () =>
            {
                AssertNull(
                    MissionService.SerializeCaptainOverrides(null),
                    "Null overrides must serialize to null");
                AssertNull(
                    MissionService.SerializeCaptainOverrides(new List<CaptainAssignmentOverride>()),
                    "Empty overrides must serialize to null, not \"[]\", which would read as configured");
            });

            await RunTest("A persisted override round-trips through the voyage row", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Voyage voyage = new Voyage("Override voyage", "Captain override persistence");
                    voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(
                        new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("Worker", "cpt_examplepersisted", CaptainTierEnum.Standard)
                        });

                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Voyage? readBack = await testDb.Driver.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(readBack, "Voyage should exist after create");
                    Assert(
                        !String.IsNullOrEmpty(readBack!.CaptainOverridesJson),
                        "The override column must persist; a null here is the original defect");

                    List<CaptainAssignmentOverride> resolved =
                        MissionService.DeserializeCaptainOverrides(readBack.CaptainOverridesJson);
                    AssertEqual(1, resolved.Count, "The resolver should see the persisted override");
                    AssertEqual("cpt_examplepersisted", resolved[0].CaptainId, "Persisted captain id");
                }
            });

            await RunTest("A voyage dispatched without overrides stores null", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Voyage voyage = new Voyage("Plain voyage", "No overrides");
                    voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(null);
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Voyage? readBack = await testDb.Driver.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(readBack, "Voyage should exist");
                    Assert(
                        String.IsNullOrEmpty(readBack!.CaptainOverridesJson),
                        "A voyage with no overrides must not carry an override payload");
                    AssertEqual(
                        0,
                        MissionService.DeserializeCaptainOverrides(readBack.CaptainOverridesJson).Count,
                        "Resolver should see no overrides");
                }
            });

            // The dispatch seam itself has no test double: VoyageDispatchService needs a live Admiral, a
            // vessel, and a pipeline to reach the persistence line. The property that actually broke was
            // that NOBODY assigned the column, so the guard asserts the assignment exists on the one path
            // both REST and MCP dispatch flow through.

            await RunTest("The shared dispatch path persists captain assignments", () =>
            {
                string root = FindRepositoryRoot();

                string service = File.ReadAllText(
                    Path.Combine(root, "src", "Armada.Server", "VoyageDispatchService.cs"));
                Assert(
                    service.Contains("voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(request.CaptainAssignments)"),
                    "VoyageDispatchService must persist the requested captain assignments onto the voyage");

                // Both callers must actually populate the shared request, or the seam above never sees them.
                string rest = File.ReadAllText(
                    Path.Combine(root, "src", "Armada.Server", "Routes", "VoyageRoutes.cs"));
                Assert(
                    rest.Contains("CaptainAssignments = request.CaptainAssignments"),
                    "The REST dispatch path must forward captain assignments into the shared request");

                string mcp = File.ReadAllText(
                    Path.Combine(root, "src", "Armada.Server", "Mcp", "Tools", "McpVoyageTools.cs"));
                Assert(
                    mcp.Contains("CaptainAssignments = request.CaptainAssignments"),
                    "The MCP dispatch path must forward captain assignments into the shared request");
                Assert(
                    mcp.Contains("captainAssignments = new"),
                    "The MCP tool must declare captainAssignments in its schema, or no operator can send it");
            });
        }

        /// <summary>
        /// Walk up from the test binary until the directory containing the solution's src folder is found,
        /// so the source guard does not depend on the working directory a runner chose.
        /// </summary>
        /// <returns>Absolute path to the repository root.</returns>
        private static string FindRepositoryRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "Armada.Server"))) return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
        }
    }
}
