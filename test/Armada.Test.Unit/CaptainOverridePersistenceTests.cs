namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Pins the serialized form of per-persona captain overrides and its round trip through the voyage
    /// row. These cases do not prove dispatch persistence: VoyageDispatchServiceTests drives the REST and
    /// MCP dispatch paths, reads the stored override, and proves assignment routes by it.
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
        }
    }
}
