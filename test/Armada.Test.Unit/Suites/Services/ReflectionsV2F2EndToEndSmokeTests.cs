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
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Reflections v2-F2 end-to-end smoke coverage. Aligned to spec acceptance
    /// criteria 5, 6, 8, 9, 11, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23.
    /// </summary>
    public class ReflectionsV2F2EndToEndSmokeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Reflections v2-F2 End-To-End Smoke";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("F2_PersonaCaptainModelFields_RoundTripThroughDatabase", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Persona persona = new Persona("F2-Architect", "persona.architect");
                persona.TenantId = Constants.DefaultTenantId;
                persona.DefaultPlaybooks = "[{\"playbookId\":\"pbk_x\",\"deliveryMode\":\"InstructionWithReference\"}]";
                persona.CurateThreshold = 8;
                persona.LearnedPlaybookId = "pbk_persona_learned_001";
                persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                Persona? round = await testDb.Driver.Personas.ReadByNameAsync("F2-Architect").ConfigureAwait(false);
                AssertNotNull(round, "Persona round-trips after Create");
                AssertEqual("[{\"playbookId\":\"pbk_x\",\"deliveryMode\":\"InstructionWithReference\"}]", round!.DefaultPlaybooks, "DefaultPlaybooks JSON survives the round-trip");
                AssertEqual(8, round.CurateThreshold ?? -1, "CurateThreshold survives the round-trip");
                AssertEqual("pbk_persona_learned_001", round.LearnedPlaybookId, "LearnedPlaybookId survives the round-trip");

                Captain captain = new Captain("F2-Worker-1", AgentRuntimeEnum.ClaudeCode);
                captain.TenantId = Constants.DefaultTenantId;
                captain.DefaultPlaybooks = "[{\"playbookId\":\"pbk_y\",\"deliveryMode\":\"InstructionWithReference\"}]";
                captain.CurateThreshold = 12;
                captain.LearnedPlaybookId = "pbk_captain_learned_001";
                captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                Captain? roundCap = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertNotNull(roundCap, "Captain round-trips after Create");
                AssertEqual("[{\"playbookId\":\"pbk_y\",\"deliveryMode\":\"InstructionWithReference\"}]", roundCap!.DefaultPlaybooks, "Captain DefaultPlaybooks survives the round-trip");
                AssertEqual(12, roundCap.CurateThreshold ?? -1, "Captain CurateThreshold survives the round-trip");
                AssertEqual("pbk_captain_learned_001", roundCap.LearnedPlaybookId, "Captain LearnedPlaybookId survives the round-trip");
            });

            await RunTest("F2_BootstrapPersonaLearned_CreatesPlaybookAndAttaches", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Persona persona = new Persona("F2-Judge", "persona.judge");
                persona.TenantId = Constants.DefaultTenantId;
                persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                logging.Settings.EnableConsole = false;
                ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                string playbookId = await svc.BootstrapPersonaAsync(persona).ConfigureAwait(false);
                AssertFalse(string.IsNullOrEmpty(playbookId), "BootstrapPersonaAsync returns a playbook id");

                Persona? reloaded = await testDb.Driver.Personas.ReadByNameAsync("F2-Judge").ConfigureAwait(false);
                AssertNotNull(reloaded, "Persona row exists after bootstrap");
                AssertEqual(playbookId, reloaded!.LearnedPlaybookId, "Persona.LearnedPlaybookId set to bootstrapped playbook id");
                List<SelectedPlaybook> defaults = reloaded.GetDefaultPlaybooks();
                AssertEqual(1, defaults.Count, "Persona.DefaultPlaybooks has the new entry");
                AssertEqual(playbookId, defaults[0].PlaybookId, "DefaultPlaybooks references the bootstrapped playbook");

                Playbook? pb = await testDb.Driver.Playbooks.ReadByFileNameAsync(Constants.DefaultTenantId, "persona-f2-judge-learned.md").ConfigureAwait(false);
                AssertNotNull(pb, "Persona-learned playbook exists with deterministic filename");

                // Idempotent: second call must not create a duplicate.
                await svc.BootstrapPersonaAsync(reloaded).ConfigureAwait(false);
                List<Playbook> all = await testDb.Driver.Playbooks.EnumerateAsync(Constants.DefaultTenantId).ConfigureAwait(false);
                int matches = 0;
                foreach (Playbook p in all)
                {
                    if (p.FileName == "persona-f2-judge-learned.md") matches++;
                }
                AssertEqual(1, matches, "BootstrapPersonaAsync is idempotent across reruns");
            });

            await RunTest("F2_HabitPatternMiner_PersonaScopeAggregatesAcrossCaptains", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain capA = new Captain("F2-cap-A", AgentRuntimeEnum.ClaudeCode) { TenantId = Constants.DefaultTenantId };
                capA = await testDb.Driver.Captains.CreateAsync(capA).ConfigureAwait(false);
                Captain capB = new Captain("F2-cap-B", AgentRuntimeEnum.Codex) { TenantId = Constants.DefaultTenantId };
                capB = await testDb.Driver.Captains.CreateAsync(capB).ConfigureAwait(false);

                Vessel vessel = new Vessel("F2-vessel-mine", "https://github.com/test/f2-mine.git") { TenantId = Constants.DefaultTenantId };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                DateTime baseTime = DateTime.UtcNow.AddHours(-2);
                for (int i = 0; i < 3; i++)
                {
                    Mission m = new Mission("F2-arch-" + i, "Architect work " + i)
                    {
                        TenantId = Constants.DefaultTenantId,
                        VesselId = vessel.Id,
                        CaptainId = (i % 2 == 0) ? capA.Id : capB.Id,
                        Persona = "Architect",
                        Status = i == 2 ? MissionStatusEnum.Failed : MissionStatusEnum.Complete,
                        FailureReason = i == 2 ? "Worker did not write any tests for the new helper class." : null,
                        CompletedUtc = baseTime.AddMinutes(i * 5)
                    };
                    await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);
                }

                HabitPatternMiner miner = new HabitPatternMiner(testDb.Driver);
                HabitPatternResult result = await miner.MinePersonaAsync("Architect", null, 100).ConfigureAwait(false);

                AssertEqual(3, result.MissionsExamined, "Three Architect missions in scope");
                AssertEqual(2, result.MissionsComplete, "Two complete");
                AssertEqual(1, result.MissionsFailed, "One failed");
                AssertEqual(2, result.CaptainContributions.Count, "Two distinct captains contributed");
                AssertTrue(result.FailureModeTags.Exists(t => t.Tag == "missing-tests"), "FailureReason matched the missing-tests tag");
            });

            await RunTest("F2_HabitPatternMiner_CaptainScopeProducesPersonaRoleDistribution", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain cap = new Captain("F2-cap-multi", AgentRuntimeEnum.ClaudeCode) { TenantId = Constants.DefaultTenantId };
                cap = await testDb.Driver.Captains.CreateAsync(cap).ConfigureAwait(false);
                Vessel vessel = new Vessel("F2-vessel-multi", "https://github.com/test/f2-multi.git") { TenantId = Constants.DefaultTenantId };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                DateTime baseTime = DateTime.UtcNow.AddHours(-2);
                string[] personas = new[] { "Architect", "Architect", "Worker", "Judge" };
                for (int i = 0; i < personas.Length; i++)
                {
                    Mission m = new Mission("F2-cap-mission-" + i, "Cap mission " + i)
                    {
                        TenantId = Constants.DefaultTenantId,
                        VesselId = vessel.Id,
                        CaptainId = cap.Id,
                        Persona = personas[i],
                        Status = MissionStatusEnum.Complete,
                        CompletedUtc = baseTime.AddMinutes(i * 3)
                    };
                    await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);
                }

                HabitPatternMiner miner = new HabitPatternMiner(testDb.Driver);
                HabitPatternResult result = await miner.MineCaptainAsync(cap.Id, null, 100).ConfigureAwait(false);

                AssertEqual(4, result.MissionsExamined, "Four missions for this captain");
                AssertTrue(result.PersonaRoleDistribution.Count >= 3, "At least three distinct persona roles surfaced");
                int architectCount = 0;
                foreach (PersonaRoleCount p in result.PersonaRoleDistribution)
                {
                    if (p.PersonaName == "Architect") architectCount = p.MissionCount;
                }
                AssertEqual(2, architectCount, "Architect contributed two missions for this captain");
            });

            await RunTest("F2_SanitizeIdentityName_HandlesCaptainIdAndPersonaName", () =>
            {
                AssertEqual("architect", ReflectionDispatcher.SanitizeIdentityName("Architect"), "PersonaName lowercased");
                AssertEqual("cpt-mouwolsu-xoddchjh252", ReflectionDispatcher.SanitizeIdentityName("cpt_mouwolsu_XodDCHJh252"), "Captain id underscores become hyphens");
                AssertEqual("unknown", ReflectionDispatcher.SanitizeIdentityName(""), "Empty input returns 'unknown'");
                return Task.CompletedTask;
            });

            await RunTest("F2_ReflectionMode_ParseAndWireRoundTripIdentityModes", () =>
            {
                AssertEqual(ReflectionMode.PersonaCurate, ReflectionMemoryService.ParseModeString("persona-curate") ?? ReflectionMode.Consolidate, "persona-curate parses");
                AssertEqual(ReflectionMode.CaptainCurate, ReflectionMemoryService.ParseModeString("captain-curate") ?? ReflectionMode.Consolidate, "captain-curate parses");
                AssertEqual("persona-curate", ReflectionMemoryService.ModeToWireString(ReflectionMode.PersonaCurate), "Wire string round-trips for persona-curate");
                AssertEqual("captain-curate", ReflectionMemoryService.ModeToWireString(ReflectionMode.CaptainCurate), "Wire string round-trips for captain-curate");
                return Task.CompletedTask;
            });

            await RunTest("F2_AdmiralLayeredMerge_PersonaDefaultPlaybookFlowsIntoSnapshots", async () =>
            {
                // Acceptance criterion 12: vessel -> persona -> captain three-way merge.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                // Seed a vessel-default playbook and a persona-default playbook.
                Playbook vesselPb = new Playbook("vessel-merge-test-learned.md", "# Vessel Learned Facts\n\nNo accepted reflection facts yet.")
                {
                    TenantId = Constants.DefaultTenantId
                };
                vesselPb = await testDb.Driver.Playbooks.CreateAsync(vesselPb).ConfigureAwait(false);
                Playbook personaPb = new Playbook("persona-architect-learned.md", "# Persona Learned Notes -- Architect\n\nNo accepted persona-curate notes yet.")
                {
                    TenantId = Constants.DefaultTenantId
                };
                personaPb = await testDb.Driver.Playbooks.CreateAsync(personaPb).ConfigureAwait(false);

                Vessel vessel = new Vessel("merge-test", "https://github.com/test/merge.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    DefaultPlaybooks = "[{\"playbookId\":\"" + vesselPb.Id + "\",\"deliveryMode\":\"InstructionWithReference\"}]"
                };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Persona persona = new Persona("Architect", "persona.architect")
                {
                    TenantId = Constants.DefaultTenantId,
                    DefaultPlaybooks = "[{\"playbookId\":\"" + personaPb.Id + "\",\"deliveryMode\":\"InstructionWithReference\"}]",
                    LearnedPlaybookId = personaPb.Id
                };
                persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                List<SelectedPlaybook> vesselDefaults = vessel.GetDefaultPlaybooks();
                List<SelectedPlaybook> personaDefaults = persona.GetDefaultPlaybooks();
                List<SelectedPlaybook> merged = PlaybookMerge.MergeWithVesselDefaults(vesselDefaults, personaDefaults);

                AssertEqual(2, merged.Count, "Three-way merge produces both vessel and persona entries when ids differ");
                bool hasVessel = merged.Exists(s => s.PlaybookId == vesselPb.Id);
                bool hasPersona = merged.Exists(s => s.PlaybookId == personaPb.Id);
                AssertTrue(hasVessel, "Vessel default playbook is present in the merged list");
                AssertTrue(hasPersona, "Persona default playbook is present in the merged list");
            });

            await RunTest("F2_AcceptIdentityCurate_LowConfidenceRejectedForPersonaScope", async () =>
            {
                // Acceptance criterion 11: persona_note_confidence_too_low.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Persona persona = new Persona("F2-arch-low", "persona.architect") { TenantId = Constants.DefaultTenantId };
                persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);
                Vessel vessel = new Vessel("F2-vessel-low", "https://github.com/test/f2-low.git") { TenantId = Constants.DefaultTenantId };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Mission mission = new Mission("Curate persona-learned", "brief")
                {
                    TenantId = Constants.DefaultTenantId,
                    VesselId = vessel.Id,
                    Persona = "MemoryConsolidator",
                    Status = MissionStatusEnum.Complete,
                    AgentOutput = "```reflections-candidate\n# F2-arch-low\n\n## Routing preferences\n[low] Pattern observed once. Source: msn_aaa, msn_bbb, msn_ccc.\n```\n```reflections-diff\n{\"added\":[{\"section\":\"Routing preferences\",\"summary\":\"weak\",\"confidence\":\"low\"}],\"modified\":[],\"disabled\":[],\"evidenceConfidence\":\"low\",\"missionsExamined\":1,\"captainsInScope\":1,\"notes\":\"weak\"}\n```"
                };
                mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                ArmadaEvent dispatched = new ArmadaEvent("reflection.dispatched", "")
                {
                    TenantId = Constants.DefaultTenantId,
                    MissionId = mission.Id,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    VesselId = vessel.Id,
                    Payload = "{\"mode\":\"persona-curate\",\"dualJudge\":false,\"targetType\":\"persona\",\"targetId\":\"F2-arch-low\"}"
                };
                await testDb.Driver.Events.CreateAsync(dispatched).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id,
                    null,
                    new ReflectionOutputParser()).ConfigureAwait(false);

                AssertEqual("persona_note_confidence_too_low", outcome.Error, "Low-confidence persona note is rejected");
            });

            await RunTest("F2_AcceptIdentityCurate_PersonaCaptainIdInNoteRejected", async () =>
            {
                // Acceptance criterion 11: persona_note_specifies_captain.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Persona persona = new Persona("F2-arch-cap", "persona.architect") { TenantId = Constants.DefaultTenantId };
                persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);
                Vessel vessel = new Vessel("F2-vessel-cap", "https://github.com/test/f2-cap.git") { TenantId = Constants.DefaultTenantId };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Mission mission = new Mission("Curate persona-learned", "brief")
                {
                    TenantId = Constants.DefaultTenantId,
                    VesselId = vessel.Id,
                    Persona = "MemoryConsolidator",
                    Status = MissionStatusEnum.Complete,
                    AgentOutput = "```reflections-candidate\n# F2-arch-cap\n\n## Anti-patterns\n[medium] Captain cpt_mouwolsu_XodDCHJh252 swallows warnings. Source: msn_aaa, msn_bbb, msn_ccc.\n```\n```reflections-diff\n{\"added\":[{\"section\":\"Anti-patterns\",\"summary\":\"swallows\",\"confidence\":\"medium\"}],\"modified\":[],\"disabled\":[],\"evidenceConfidence\":\"mixed\",\"missionsExamined\":3,\"captainsInScope\":1,\"notes\":\"x\"}\n```"
                };
                mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                ArmadaEvent dispatched = new ArmadaEvent("reflection.dispatched", "")
                {
                    TenantId = Constants.DefaultTenantId,
                    MissionId = mission.Id,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    VesselId = vessel.Id,
                    Payload = "{\"mode\":\"persona-curate\",\"dualJudge\":false,\"targetType\":\"persona\",\"targetId\":\"F2-arch-cap\"}"
                };
                await testDb.Driver.Events.CreateAsync(dispatched).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id,
                    null,
                    new ReflectionOutputParser()).ConfigureAwait(false);

                AssertEqual("persona_note_specifies_captain", outcome.Error, "Captain-id-in-persona-note is rejected");
            });

            await RunTest("F2_AcceptCaptainCurate_LazyCreatesLearnedPlaybookAndWiresDefaults", async () =>
            {
                // Acceptance criterion 20: captain learned playbook lazy-created on first accept.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);

                Captain captain = new Captain("F2-Worker-Solo", AgentRuntimeEnum.ClaudeCode) { TenantId = Constants.DefaultTenantId };
                captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                AssertTrue(string.IsNullOrEmpty(captain.LearnedPlaybookId), "LearnedPlaybookId starts null");

                Vessel vessel = new Vessel("F2-vessel-cap-accept", "https://github.com/test/f2-cap-accept.git") { TenantId = Constants.DefaultTenantId };
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Mission mission = new Mission("Curate captain-learned", "brief")
                {
                    TenantId = Constants.DefaultTenantId,
                    VesselId = vessel.Id,
                    Persona = "MemoryConsolidator",
                    Status = MissionStatusEnum.Complete,
                    AgentOutput = "```reflections-candidate\n# Captain F2-Worker-Solo\n\n## Routing preferences\n[medium] Reliable on SPN-decode work. Source: msn_aaa, msn_bbb, msn_ccc.\n```\n```reflections-diff\n{\"added\":[{\"section\":\"Routing preferences\",\"summary\":\"spn-decode\",\"confidence\":\"medium\"}],\"modified\":[],\"disabled\":[],\"evidenceConfidence\":\"mixed\",\"missionsExamined\":3,\"notes\":\"x\"}\n```"
                };
                mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                ArmadaEvent dispatched = new ArmadaEvent("reflection.dispatched", "")
                {
                    TenantId = Constants.DefaultTenantId,
                    MissionId = mission.Id,
                    EntityType = "mission",
                    EntityId = mission.Id,
                    VesselId = vessel.Id,
                    Payload = "{\"mode\":\"captain-curate\",\"dualJudge\":false,\"targetType\":\"captain\",\"targetId\":\"" + captain.Id + "\"}"
                };
                await testDb.Driver.Events.CreateAsync(dispatched).ConfigureAwait(false);

                ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                    mission.Id,
                    null,
                    new ReflectionOutputParser()).ConfigureAwait(false);

                AssertTrue(string.IsNullOrEmpty(outcome.Error), "Accept proposal succeeded; error=" + (outcome.Error ?? ""));
                AssertFalse(string.IsNullOrEmpty(outcome.PlaybookId), "Captain-learned playbook id returned");

                Captain? reloaded = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Captain row still exists after accept");
                AssertEqual(outcome.PlaybookId, reloaded!.LearnedPlaybookId, "Captain.LearnedPlaybookId points at the new playbook");
                List<SelectedPlaybook> defaults = reloaded.GetDefaultPlaybooks();
                AssertEqual(1, defaults.Count, "Captain.DefaultPlaybooks now lists the learned playbook");
                AssertEqual(outcome.PlaybookId, defaults[0].PlaybookId, "DefaultPlaybooks entry references the new captain-learned playbook");

                string expectedFileName = "captain-" + ReflectionDispatcher.SanitizeIdentityName(captain.Id) + "-learned.md";
                Playbook? created = await testDb.Driver.Playbooks.ReadByFileNameAsync(Constants.DefaultTenantId, expectedFileName).ConfigureAwait(false);
                AssertNotNull(created, "Captain-learned playbook persisted with sanitized filename");
            });
        }
    }
}
