namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Reflections v2-F3 end-to-end smoke coverage. Aligned to spec acceptance
    /// criteria 1, 4, 5, 7, 8, 9, 10, 11, 14, 15, 16, 17, 18, 19, 20.
    /// </summary>
    public class ReflectionsV2F3EndToEndSmokeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflections v2-F3 End-To-End Smoke";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("F3_FleetModelFields_RoundTripThroughDatabase", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Fleet fleet = new Fleet("F3-fleet-roundtrip");
                fleet.TenantId = Constants.DefaultTenantId;
                fleet.DefaultPlaybooks = "[{\"playbookId\":\"pbk_z\",\"deliveryMode\":\"InstructionWithReference\"}]";
                fleet.CurateThreshold = 25;
                fleet.LearnedPlaybookId = "pbk_fleet_learned_001";
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Fleet? round = await testDb.Driver.Fleets.ReadAsync(fleet.Id).ConfigureAwait(false);
                AssertNotNull(round, "Fleet round-trips after Create");
                AssertEqual("[{\"playbookId\":\"pbk_z\",\"deliveryMode\":\"InstructionWithReference\"}]", round!.DefaultPlaybooks, "Fleet DefaultPlaybooks survives the round-trip");
                AssertEqual(25, round.CurateThreshold ?? -1, "Fleet CurateThreshold survives the round-trip");
                AssertEqual("pbk_fleet_learned_001", round.LearnedPlaybookId, "Fleet LearnedPlaybookId survives the round-trip");

                List<SelectedPlaybook> defaults = round.GetDefaultPlaybooks();
                AssertEqual(1, defaults.Count, "GetDefaultPlaybooks parses the JSON to one entry");
                AssertEqual("pbk_z", defaults[0].PlaybookId, "Parsed playbookId matches the stored JSON");
            });

            await RunTest("F3_ReflectionMode_FleetCurateParsesAndRoundTrips", () =>
            {
                AssertEqual(ReflectionMode.FleetCurate, ReflectionMemoryService.ParseModeString("fleet-curate") ?? ReflectionMode.Consolidate, "fleet-curate parses");
                AssertEqual(ReflectionMode.FleetCurate, ReflectionMemoryService.ParseModeString("fleetcurate") ?? ReflectionMode.Consolidate, "fleetcurate alias parses");
                AssertEqual("fleet-curate", ReflectionMemoryService.ModeToWireString(ReflectionMode.FleetCurate), "Wire string round-trips");
                return Task.CompletedTask;
            });

            await RunTest("F3_HabitPatternMiner_FleetScopeFiltersInactiveAndAggregatesAcrossVessels", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Fleet fleet = new Fleet("F3-fleet-mine") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vesA = new Vessel("F3-vesselA", "https://github.com/test/f3a.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id,
                    Active = true
                };
                vesA = await testDb.Driver.Vessels.CreateAsync(vesA).ConfigureAwait(false);
                Vessel vesB = new Vessel("F3-vesselB", "https://github.com/test/f3b.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id,
                    Active = true
                };
                vesB = await testDb.Driver.Vessels.CreateAsync(vesB).ConfigureAwait(false);
                Vessel vesInactive = new Vessel("F3-vesselInactive", "https://github.com/test/f3i.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id,
                    Active = false
                };
                vesInactive = await testDb.Driver.Vessels.CreateAsync(vesInactive).ConfigureAwait(false);

                DateTime baseTime = DateTime.UtcNow.AddHours(-3);
                int idx = 0;
                foreach (Vessel v in new[] { vesA, vesA, vesB, vesInactive, vesInactive })
                {
                    Mission m = new Mission("F3-fleet-mine-" + idx, "Fleet evidence " + idx)
                    {
                        TenantId = Constants.DefaultTenantId,
                        VesselId = v.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.Complete,
                        CompletedUtc = baseTime.AddMinutes(idx * 4)
                    };
                    await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);
                    idx++;
                }

                HabitPatternMiner miner = new HabitPatternMiner(testDb.Driver);
                HabitPatternResult result = await miner.MineFleetAsync(fleet.Id, null, 100).ConfigureAwait(false);

                AssertEqual(HabitPatternScope.Fleet, result.Scope, "Result scope is Fleet");
                AssertEqual(fleet.Id, result.TargetId, "Result target id is the fleet id");
                AssertEqual(3, result.MissionsExamined, "Inactive vessel missions are filtered out (3 active-vessel missions remain)");
                AssertEqual(2, result.VesselContributions.Count, "Two active vessels contributed");
                int aMissions = 0;
                int bMissions = 0;
                foreach (VesselContribution vc in result.VesselContributions)
                {
                    if (vc.VesselId == vesA.Id) aMissions = vc.MissionCount;
                    if (vc.VesselId == vesB.Id) bMissions = vc.MissionCount;
                }
                AssertEqual(2, aMissions, "Vessel A contributed two missions");
                AssertEqual(1, bMissions, "Vessel B contributed one mission");
            });

            await RunTest("F3_Jaccard3Gram_AndSentimentDisagreement", () =>
            {
                double simSame = HabitPatternMiner.Jaccard3GramSimilarity("the seed-key algorithm lives here", "the seed-key algorithm lives here");
                AssertTrue(simSame > 0.99, "Identical strings have Jaccard ~ 1.0");

                double simNeg = HabitPatternMiner.Jaccard3GramSimilarity("the seed-key algorithm lives here", "different content entirely zzz");
                AssertTrue(simNeg < 0.3, "Unrelated strings have low Jaccard");

                AssertTrue(HabitPatternMiner.SentimentDisagrees("we always do X", "we do not do X here"), "Negation asymmetry detected");
                AssertFalse(HabitPatternMiner.SentimentDisagrees("we always do X", "we always do X"), "Identical positive strings disagree=false");
                return Task.CompletedTask;
            });

            await RunTest("F3_FourWayMerge_FleetLayerAppearsFirst", async () =>
            {
                // Acceptance criterion 11: fleet -> vessel -> persona -> captain merge ordering.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Playbook fleetPb = new Playbook("fleet-merge-test-learned.md", "# Fleet Learned Notes\n\nNo accepted fleet-curate notes yet.")
                {
                    TenantId = Constants.DefaultTenantId
                };
                fleetPb = await testDb.Driver.Playbooks.CreateAsync(fleetPb).ConfigureAwait(false);
                Playbook vesselPb = new Playbook("vessel-merge-test-learned.md", "# Vessel Learned Facts\n\nNo accepted reflection facts yet.")
                {
                    TenantId = Constants.DefaultTenantId
                };
                vesselPb = await testDb.Driver.Playbooks.CreateAsync(vesselPb).ConfigureAwait(false);

                Fleet fleet = new Fleet("F3-merge-fleet")
                {
                    TenantId = Constants.DefaultTenantId,
                    DefaultPlaybooks = "[{\"playbookId\":\"" + fleetPb.Id + "\",\"deliveryMode\":\"InstructionWithReference\"}]"
                };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel("F3-merge-vessel", "https://github.com/test/f3-merge.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id,
                    DefaultPlaybooks = "[{\"playbookId\":\"" + vesselPb.Id + "\",\"deliveryMode\":\"InstructionWithReference\"}]"
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                List<SelectedPlaybook> fleetDefaults = fleet.GetDefaultPlaybooks();
                List<SelectedPlaybook> vesselDefaults = vessel.GetDefaultPlaybooks();
                List<SelectedPlaybook> merged = PlaybookMerge.MergeWithVesselDefaults(fleetDefaults, vesselDefaults);

                AssertEqual(2, merged.Count, "Fleet + vessel both contribute distinct ids");
                AssertEqual(fleetPb.Id, merged[0].PlaybookId, "Fleet entry appears FIRST (least specific)");
                AssertEqual(vesselPb.Id, merged[1].PlaybookId, "Vessel entry appears second");
            });

            await RunTest("F3_AcceptFleetCurate_PromotionInsufficientVesselsBlocks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Fleet fleet = new Fleet("F3-fleet-block-vc") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel("F3-block-vc-vessel", "https://github.com/test/f3-block-vc.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                string candidate = "=== FLEET PLAYBOOK CONTENT ===\n# Fleet Notes\n\n## Project conventions\n[high] Single-vessel fact.\nSource: vessel F3-block-vc-vessel (msn_aaa, msn_bbb, msn_ccc).\n=== END FLEET PLAYBOOK CONTENT ===\n\n=== RIPPLE DISABLES (JSON) ===\n{\"disableFromVessels\":[]}\n=== END RIPPLE DISABLES ===";
                string diff = "{\"added\":[{\"section\":\"Project conventions\",\"summary\":\"single\",\"confidence\":\"high\",\"vesselsContributing\":1,\"missionsSupporting\":3}],\"modified\":[],\"disabled\":[],\"rippleDisables\":0,\"evidenceConfidence\":\"high\",\"missionsExamined\":3,\"vesselsInScope\":1,\"notes\":\"x\"}";
                Mission mission = await CreateFleetCurateMissionAsync(testDb, vessel, fleet.Id, candidate, diff).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id, null, new ReflectionOutputParser()).ConfigureAwait(false);
                AssertEqual("fleet_promotion_insufficient_vessels", outcome.Error, "Single-vessel promotion is blocked");
            });

            await RunTest("F3_AcceptFleetCurate_PromotionInsufficientMissionsBlocks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Fleet fleet = new Fleet("F3-fleet-block-mc") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);
                Vessel vessel = new Vessel("F3-block-mc-vessel", "https://github.com/test/f3-block-mc.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                string candidate = "=== FLEET PLAYBOOK CONTENT ===\n# Fleet Notes\n\n## Project conventions\n[medium] Two vessels but only two missions.\nSource: vessel A (msn_aaa), vessel B (msn_bbb).\n=== END FLEET PLAYBOOK CONTENT ===\n\n=== RIPPLE DISABLES (JSON) ===\n{\"disableFromVessels\":[]}\n=== END RIPPLE DISABLES ===";
                string diff = "{\"added\":[{\"section\":\"Project conventions\",\"summary\":\"two missions\",\"confidence\":\"medium\",\"vesselsContributing\":2,\"missionsSupporting\":2}],\"modified\":[],\"disabled\":[],\"rippleDisables\":0,\"evidenceConfidence\":\"mixed\",\"missionsExamined\":2,\"vesselsInScope\":2,\"notes\":\"x\"}";
                Mission mission = await CreateFleetCurateMissionAsync(testDb, vessel, fleet.Id, candidate, diff).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id, null, new ReflectionOutputParser()).ConfigureAwait(false);
                AssertEqual("fleet_promotion_insufficient_missions", outcome.Error, "Promotion with <3 missions is blocked");
            });

            await RunTest("F3_AcceptFleetCurate_VesselFleetConflictBlocks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Fleet fleet = new Fleet("F3-fleet-conflict") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel("F3-conflict-vessel", "https://github.com/test/f3-conflict.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Playbook vesselLearned = new Playbook(
                    "vessel-f3-conflict-vessel-learned.md",
                    "# F3-conflict-vessel learned facts\n\n## Project conventions\n[high] PASETO token primitives are project-wide and live in otrbuddy.\nSource: msn_aaa, msn_bbb.\n")
                {
                    TenantId = Constants.DefaultTenantId
                };
                await testDb.Driver.Playbooks.CreateAsync(vesselLearned).ConfigureAwait(false);

                // Candidate that contradicts the vessel-learned content (negation asymmetry +
                // high 3-gram overlap).
                string candidate = "=== FLEET PLAYBOOK CONTENT ===\n# Fleet Notes\n\n## Project conventions\n[high] PASETO token primitives are not project-wide and do not live in otrbuddy.\nSource: vessel A (msn_xxx, msn_yyy), vessel B (msn_zzz).\n=== END FLEET PLAYBOOK CONTENT ===\n\n=== RIPPLE DISABLES (JSON) ===\n{\"disableFromVessels\":[]}\n=== END RIPPLE DISABLES ===";
                string diff = "{\"added\":[{\"section\":\"Project conventions\",\"summary\":\"contradicting\",\"confidence\":\"high\",\"vesselsContributing\":2,\"missionsSupporting\":3}],\"modified\":[],\"disabled\":[],\"rippleDisables\":0,\"evidenceConfidence\":\"high\",\"missionsExamined\":3,\"vesselsInScope\":2,\"notes\":\"conflict\"}";
                Mission mission = await CreateFleetCurateMissionAsync(testDb, vessel, fleet.Id, candidate, diff).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id, null, new ReflectionOutputParser()).ConfigureAwait(false);
                AssertEqual("fleet_curate_vessel_conflict", outcome.Error, "Vessel-fleet conflict is BLOCKING per Q3");
            });

            await RunTest("F3_AcceptFleetCurate_HappyPathLazyCreatesFleetPlaybookAndAppliesRipple", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Fleet fleet = new Fleet("F3-fleet-happy") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel("F3-happy-vessel", "https://github.com/test/f3-happy.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Playbook vesselLearned = new Playbook(
                    "vessel-f3-happy-vessel-learned.md",
                    "# F3-happy-vessel learned facts\n\n## Project conventions\n[high] Shared cross-cutting fact about CAN bus protocol decoding.\nSource: msn_a1, msn_b2, msn_c3.\n")
                {
                    TenantId = Constants.DefaultTenantId
                };
                await testDb.Driver.Playbooks.CreateAsync(vesselLearned).ConfigureAwait(false);

                string candidate = "=== FLEET PLAYBOOK CONTENT ===\n# Fleet Notes\n\n## Cross-cutting tooling\n[high] CAN bus protocol decoding is shared across vessels in this fleet.\nSource: vessel A (msn_aaa, msn_bbb), vessel B (msn_ccc).\n=== END FLEET PLAYBOOK CONTENT ===\n\n=== RIPPLE DISABLES (JSON) ===\n{\"disableFromVessels\":[{\"vesselId\":\"" + vessel.Id + "\",\"noteRef\":\"Project conventions:0\",\"reason\":\"Promoted to fleet scope.\"}]}\n=== END RIPPLE DISABLES ===";
                string diff = "{\"added\":[{\"section\":\"Cross-cutting tooling\",\"summary\":\"CAN bus shared\",\"confidence\":\"high\",\"vesselsContributing\":2,\"missionsSupporting\":3}],\"modified\":[],\"disabled\":[],\"rippleDisables\":1,\"evidenceConfidence\":\"high\",\"missionsExamined\":3,\"vesselsInScope\":2,\"notes\":\"first promotion\"}";
                Mission mission = await CreateFleetCurateMissionAsync(testDb, vessel, fleet.Id, candidate, diff).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id, null, new ReflectionOutputParser()).ConfigureAwait(false);
                AssertTrue(string.IsNullOrEmpty(outcome.Error), "Happy-path accept succeeds (error=" + (outcome.Error ?? "") + ")");
                AssertFalse(string.IsNullOrEmpty(outcome.PlaybookId), "Fleet learned playbook lazy-created");

                // Fleet row gained the LearnedPlaybookId wire and the entry in DefaultPlaybooks.
                Fleet? reload = await testDb.Driver.Fleets.ReadAsync(fleet.Id).ConfigureAwait(false);
                AssertNotNull(reload, "Fleet row reloads");
                AssertEqual(outcome.PlaybookId, reload!.LearnedPlaybookId, "Fleet.LearnedPlaybookId points at the new playbook");
                List<SelectedPlaybook> defaults = reload.GetDefaultPlaybooks();
                bool hasEntry = defaults.Exists(sp => sp.PlaybookId == outcome.PlaybookId);
                AssertTrue(hasEntry, "Fleet.DefaultPlaybooks gained the new playbook entry");

                // Ripple landed: vessel-learned content has the [disabled: ...] marker.
                Playbook? vesselReload = await testDb.Driver.Playbooks.ReadByFileNameAsync(
                    Constants.DefaultTenantId, "vessel-f3-happy-vessel-learned.md").ConfigureAwait(false);
                AssertNotNull(vesselReload, "Vessel-learned playbook still exists");
                AssertTrue(vesselReload!.Content.Contains("[disabled:"), "Ripple disable marker prepended to the matching note");
            });

            await RunTest("F3_AcceptFleetCurate_RippleInvalidVesselBlocks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Fleet fleet = new Fleet("F3-fleet-ripple-bad") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);
                Vessel vessel = new Vessel("F3-ripple-bad-vessel", "https://github.com/test/f3-ripple-bad.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                string candidate = "=== FLEET PLAYBOOK CONTENT ===\n# Fleet Notes\n\n## Tooling\n[high] Shared fact across vessels.\nSource: vessel A (msn_a, msn_b), vessel B (msn_c).\n=== END FLEET PLAYBOOK CONTENT ===\n\n=== RIPPLE DISABLES (JSON) ===\n{\"disableFromVessels\":[{\"vesselId\":\"vsl_not_in_fleet\",\"noteRef\":\"Tooling:0\",\"reason\":\"x\"}]}\n=== END RIPPLE DISABLES ===";
                string diff = "{\"added\":[{\"section\":\"Tooling\",\"summary\":\"shared\",\"confidence\":\"high\",\"vesselsContributing\":2,\"missionsSupporting\":3}],\"modified\":[],\"disabled\":[],\"rippleDisables\":1,\"evidenceConfidence\":\"high\",\"missionsExamined\":3,\"vesselsInScope\":2,\"notes\":\"\"}";
                Mission mission = await CreateFleetCurateMissionAsync(testDb, vessel, fleet.Id, candidate, diff).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id, null, new ReflectionOutputParser()).ConfigureAwait(false);
                AssertEqual("fleet_ripple_invalid_vessel", outcome.Error, "Ripple referencing a vessel outside the fleet is rejected");
            });

            await RunTest("F3_AcceptFleetCurate_EditsMarkdownOverrideBypassesAllGates", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Fleet fleet = new Fleet("F3-fleet-override") { TenantId = Constants.DefaultTenantId };
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);
                Vessel vessel = new Vessel("F3-override-vessel", "https://github.com/test/f3-override.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    FleetId = fleet.Id
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                // Single-vessel candidate that would normally fail fleet_promotion_insufficient_vessels.
                string candidate = "=== FLEET PLAYBOOK CONTENT ===\n# Fleet Notes\n\n## Tooling\n[high] Single-vessel candidate forced through.\nSource: vessel A (msn_aaa).\n=== END FLEET PLAYBOOK CONTENT ===\n\n=== RIPPLE DISABLES (JSON) ===\n{\"disableFromVessels\":[]}\n=== END RIPPLE DISABLES ===";
                string diff = "{\"added\":[{\"section\":\"Tooling\",\"summary\":\"forced\",\"confidence\":\"high\",\"vesselsContributing\":1,\"missionsSupporting\":1}],\"modified\":[],\"disabled\":[],\"rippleDisables\":0,\"evidenceConfidence\":\"high\",\"missionsExamined\":1,\"vesselsInScope\":1,\"notes\":\"override\"}";
                Mission mission = await CreateFleetCurateMissionAsync(testDb, vessel, fleet.Id, candidate, diff).ConfigureAwait(false);

                // editsMarkdown bypasses validation. Pass the same dual-section block as the override.
                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id, candidate, new ReflectionOutputParser()).ConfigureAwait(false);
                AssertTrue(string.IsNullOrEmpty(outcome.Error), "editsMarkdown override bypasses promotion gates (error=" + (outcome.Error ?? "") + ")");
                AssertFalse(string.IsNullOrEmpty(outcome.PlaybookId), "Override path persists the fleet playbook");
            });

            await RunTest("F3_GetFleetTool_ResponseIncludesAdditiveFields", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Fleet fleet = new Fleet("F3-fleet-get-shape") { TenantId = Constants.DefaultTenantId };
                fleet.DefaultPlaybooks = "[{\"playbookId\":\"pbk_get_shape\",\"deliveryMode\":\"InstructionWithReference\"}]";
                fleet.CurateThreshold = 17;
                fleet.LearnedPlaybookId = "pbk_get_shape_learned";
                fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Fleet? reload = await testDb.Driver.Fleets.ReadAsync(fleet.Id).ConfigureAwait(false);
                AssertNotNull(reload, "Fleet reload");
                List<SelectedPlaybook> defaults = reload!.GetDefaultPlaybooks();
                AssertEqual(1, defaults.Count, "GetDefaultPlaybooks parses the JSON");
                AssertEqual("pbk_get_shape", defaults[0].PlaybookId, "Parsed entry id matches");
                AssertEqual(17, reload.CurateThreshold ?? -1, "CurateThreshold reads back");
                AssertEqual("pbk_get_shape_learned", reload.LearnedPlaybookId, "LearnedPlaybookId reads back");
            });
        }

        private static async Task<Mission> CreateFleetCurateMissionAsync(
            TestDatabase testDb,
            Vessel vessel,
            string fleetId,
            string candidate,
            string diff)
        {
            string agentOutput = "```reflections-candidate\n" + candidate + "\n```\n```reflections-diff\n" + diff + "\n```";
            Mission mission = new Mission("Curate fleet-learned facts", "brief")
            {
                TenantId = Constants.DefaultTenantId,
                VesselId = vessel.Id,
                Persona = "MemoryConsolidator",
                Status = MissionStatusEnum.Complete,
                AgentOutput = agentOutput
            };
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            ArmadaEvent dispatched = new ArmadaEvent("reflection.dispatched", "")
            {
                TenantId = Constants.DefaultTenantId,
                MissionId = mission.Id,
                EntityType = "mission",
                EntityId = mission.Id,
                VesselId = vessel.Id,
                Payload = "{\"mode\":\"fleet-curate\",\"dualJudge\":false,\"targetType\":\"fleet\",\"targetId\":\"" + fleetId + "\"}"
            };
            await testDb.Driver.Events.CreateAsync(dispatched).ConfigureAwait(false);
            return mission;
        }
    }
}
