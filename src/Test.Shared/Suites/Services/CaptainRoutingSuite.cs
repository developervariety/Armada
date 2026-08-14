namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for per-step captain selection: the voyage captain-override serialization contract and the
    /// persistence round-trip of the persona default captain, mission requested captain, and voyage captain
    /// overrides across create/read/update. Covers both positive round-trips and negative (null / malformed)
    /// inputs.
    /// </summary>
    public sealed class CaptainRoutingSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.CaptainRouting";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Captain Routing suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("overrides_serialize_roundtrip", "Captain overrides serialize and deserialize round-trip", TestTags.Positive, () =>
            {
                List<CaptainAssignmentOverride> overrides = new List<CaptainAssignmentOverride>
                {
                    new CaptainAssignmentOverride("Worker", "cpt_worker", CaptainTierEnum.Economy),
                    new CaptainAssignmentOverride("Judge", "cpt_judge", CaptainTierEnum.Premium),
                    new CaptainAssignmentOverride("Architect", null, CaptainTierEnum.Standard)
                };

                string? json = MissionService.SerializeCaptainOverrides(overrides);
                AssertNotNull(json, "Serialized overrides should not be null");

                List<CaptainAssignmentOverride> parsed = MissionService.DeserializeCaptainOverrides(json);
                AssertEqual(3, parsed.Count, "Round-trip should preserve every override");
                AssertEqual("Worker", parsed[0].Persona, "First persona should round-trip");
                AssertEqual("cpt_worker", parsed[0].CaptainId, "First captain id should round-trip");
                AssertEqual(CaptainTierEnum.Economy, parsed[0].FallbackTier, "First fallback tier should round-trip");
                AssertNull(parsed[2].CaptainId, "A tier-only override should round-trip with a null captain id");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("empty_overrides_serialize_to_null", "Empty or null overrides serialize to null (column stays null)", TestTags.Positive, () =>
            {
                AssertNull(MissionService.SerializeCaptainOverrides(null), "Null overrides should serialize to null");
                AssertNull(MissionService.SerializeCaptainOverrides(new List<CaptainAssignmentOverride>()), "Empty overrides should serialize to null");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("malformed_overrides_deserialize_to_empty", "Malformed override JSON deserializes to an empty list, never throws", TestTags.Negative, () =>
            {
                AssertEqual(0, MissionService.DeserializeCaptainOverrides("this is not json").Count, "Malformed JSON should yield an empty list");
                AssertEqual(0, MissionService.DeserializeCaptainOverrides(null).Count, "Null JSON should yield an empty list");
                AssertEqual(0, MissionService.DeserializeCaptainOverrides("   ").Count, "Whitespace JSON should yield an empty list");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("persona_default_captain_persists", "Persona default captain persists and round-trips through create and read", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain captain = new Captain("routing-default");
                    captain.Tier = CaptainTierEnum.Premium;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Persona persona = new Persona("Worker", "persona.worker");
                    persona.DefaultCaptainId = captain.Id;
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Persona should be readable");
                    AssertEqual(captain.Id, read!.DefaultCaptainId, "Default captain id should persist");

                    read.DefaultCaptainId = null;
                    await testDb.Driver.Personas.UpdateAsync(read).ConfigureAwait(false);
                    Persona? cleared = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNull(cleared!.DefaultCaptainId, "Clearing the default captain should persist as null");
                }
            }));

            cases.Add(CaseAsync("persona_null_default_persists_as_null", "Persona with no default captain persists as null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Persona persona = new Persona("Judge", "persona.judge");
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Persona should be readable");
                    AssertNull(read!.DefaultCaptainId, "Default captain should be null when unset");
                }
            }));

            cases.Add(CaseAsync("mission_requested_captain_persists", "Mission requested captain persists and round-trips", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("routing mission", "route me");
                    mission.RequestedCaptainId = "cpt_preferred";
                    mission.Tier = CaptainTierEnum.Premium;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Mission should be readable");
                    AssertEqual("cpt_preferred", read!.RequestedCaptainId, "Requested captain id should persist");
                    AssertEqual(CaptainTierEnum.Premium, read.Tier, "Fallback tier should persist");
                }
            }));

            cases.Add(CaseAsync("mission_null_requested_captain_persists_as_null", "Mission with no requested captain persists as null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("unrouted mission", "no preference");
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Mission should be readable");
                    AssertNull(read!.RequestedCaptainId, "Requested captain should be null when unset");
                }
            }));

            cases.Add(CaseAsync("voyage_overrides_persist", "Voyage captain overrides persist and deserialize back to the same entries", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    List<CaptainAssignmentOverride> overrides = new List<CaptainAssignmentOverride>
                    {
                        new CaptainAssignmentOverride("Worker", "cpt_w", CaptainTierEnum.Economy)
                    };

                    Voyage voyage = new Voyage("routing voyage", "carries overrides");
                    voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(overrides);
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Voyage? read = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Voyage should be readable");
                    AssertNotNull(read!.CaptainOverridesJson, "Overrides JSON should persist");

                    List<CaptainAssignmentOverride> parsed = MissionService.DeserializeCaptainOverrides(read.CaptainOverridesJson);
                    AssertEqual(1, parsed.Count, "Persisted overrides should deserialize");
                    AssertEqual("Worker", parsed[0].Persona, "Persisted persona should round-trip");
                    AssertEqual("cpt_w", parsed[0].CaptainId, "Persisted captain id should round-trip");
                    AssertEqual(CaptainTierEnum.Economy, parsed[0].FallbackTier, "Persisted fallback tier should round-trip");
                }
            }));

            return new TestSuiteDescriptor(SuiteId, "Captain routing and per-step selection", cases);
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
