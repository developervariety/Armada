namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Covers the one bounded continuation after a captain refuses work its owner policy authorizes: the
    /// refusal is always recorded, the mission moves to an approved captain on a different runtime at most
    /// once, and it is never retried on the runtime that refused.
    /// </summary>
    public sealed class PolicyRefusalContinuationServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Policy Refusal Continuation";

        private static Captain MakeCaptain(string name, AgentRuntimeEnum runtime)
        {
            Captain captain = new Captain(name);
            captain.Runtime = runtime;
            captain.State = CaptainStateEnum.Idle;
            return captain;
        }

        private static CaptainRefusal Declared()
        {
            return CaptainRefusalClassifier.Classify(CaptainRefusalClassifier.RefusalMarker + ": inspecting the sample binary is not something I will do");
        }

        private static Mission MakeMission()
        {
            Mission mission = new Mission("Read ExampleFormat section tables", "Implement a reader for the ExampleFormat section table.");
            mission.Persona = "Worker";
            return mission;
        }

        /// <summary>Runs the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A refusal with no supplied owner policy is not a policy conflict", () =>
            {
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode);
                List<Captain> captains = new List<Captain> { refusing, MakeCaptain("alternate", AgentRuntimeEnum.Codex) };

                PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                    MakeMission(), refusing, Declared(), policyPresent: false, continuationAlreadyUsed: false, captains, null);

                AssertEqual(PolicyRefusalContinuationOutcomeEnum.NotApplicable, decision.Outcome, "no policy means normal completion handling");
                AssertFalse(String.IsNullOrEmpty(decision.Reason), "the decision must carry a reason");
            });

            await RunTest("A refusal of authorized work continues on an approved alternate runtime and excludes the refusing runtime", () =>
            {
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode);
                Captain sameRuntimePeer = MakeCaptain("same-runtime-peer", AgentRuntimeEnum.ClaudeCode);
                Captain alternate = MakeCaptain("alternate", AgentRuntimeEnum.Codex);
                List<Captain> captains = new List<Captain> { refusing, sameRuntimePeer, alternate };

                PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                    MakeMission(), refusing, Declared(), policyPresent: true, continuationAlreadyUsed: false, captains, null);

                AssertEqual(PolicyRefusalContinuationOutcomeEnum.Continue, decision.Outcome, "an approved alternate runtime receives the continuation");
                AssertTrue(decision.ExcludedCaptainIds.Contains(refusing.Id), "the refusing captain is excluded");
                AssertTrue(decision.ExcludedCaptainIds.Contains(sameRuntimePeer.Id), "every captain on the refusing runtime is excluded");
                AssertFalse(decision.ExcludedCaptainIds.Contains(alternate.Id), "the alternate-runtime captain stays eligible");
                AssertEqual("Codex", String.Join(",", decision.AlternateRuntimes), "the alternate runtime is named");
            });

            await RunTest("A second refusal after the continuation stops instead of retrying", () =>
            {
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.Codex);
                List<Captain> captains = new List<Captain> { refusing, MakeCaptain("other", AgentRuntimeEnum.ClaudeCode) };

                PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                    MakeMission(), refusing, Declared(), policyPresent: true, continuationAlreadyUsed: true, captains, null);

                AssertEqual(PolicyRefusalContinuationOutcomeEnum.Stop, decision.Outcome, "the continuation is bounded to one");
            });

            await RunTest("With no approved captain on another runtime the refused work is not retried", () =>
            {
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode);
                Captain wrongPersona = MakeCaptain("judge-only", AgentRuntimeEnum.Codex);
                wrongPersona.AllowedPersonas = "[\"Judge\"]";
                Captain benched = MakeCaptain("benched", AgentRuntimeEnum.Cursor);
                benched.State = CaptainStateEnum.Benched;
                List<Captain> captains = new List<Captain> { refusing, MakeCaptain("same-runtime", AgentRuntimeEnum.ClaudeCode), wrongPersona, benched };

                PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                    MakeMission(), refusing, Declared(), policyPresent: true, continuationAlreadyUsed: false, captains, null);

                AssertEqual(PolicyRefusalContinuationOutcomeEnum.Stop, decision.Outcome, "a same-runtime peer, an ineligible persona and a benched captain are not approved alternates");
                AssertContains("ClaudeCode", decision.Reason, "the reason names the refusing runtime");
            });

            await RunTest("A pinned model no alternate runs is a tier floor for the continuation, as it is for assignment", () =>
            {
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode);
                refusing.Model = "claude-opus-4-7";
                refusing.Tier = CaptainTierEnum.Premium;
                Captain premiumAlternate = MakeCaptain("premium-alternate", AgentRuntimeEnum.Codex);
                premiumAlternate.Model = "gpt-5.6-sol";
                premiumAlternate.Tier = CaptainTierEnum.Premium;
                Mission mission = MakeMission();
                mission.PreferredModel = "claude-opus-4-7";

                PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                    mission, refusing, Declared(), policyPresent: true, continuationAlreadyUsed: false,
                    new List<Captain> { refusing, premiumAlternate }, new ModelTierSettings());

                AssertEqual(PolicyRefusalContinuationOutcomeEnum.Continue, decision.Outcome,
                    "a Premium alternate meets the Premium floor of a pinned model only the refusing runtime runs: " + decision.Reason);
                AssertEqual("Codex", String.Join(",", decision.AlternateRuntimes), "the alternate runtime is named");
            });

            await RunTest("Every alternate captain is approved exactly when the assignment selector could choose it", () =>
            {
                ModelTierSettings tiers = new ModelTierSettings();
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode);
                refusing.Model = "claude-opus-4-7";
                refusing.Tier = CaptainTierEnum.Premium;

                List<Captain> alternates = new List<Captain>();
                foreach (CaptainTierEnum tier in new[] { CaptainTierEnum.Economy, CaptainTierEnum.Standard, CaptainTierEnum.Premium })
                {
                    foreach (string model in new[] { "claude-opus-4-7", "gpt-5.6-sol", "example-model" })
                    {
                        Captain open = MakeCaptain("open-" + tier + "-" + model, AgentRuntimeEnum.Codex);
                        open.Model = model;
                        open.Tier = tier;
                        alternates.Add(open);
                        Captain locked = MakeCaptain("judge-only-" + tier + "-" + model, AgentRuntimeEnum.Codex);
                        locked.Model = model;
                        locked.Tier = tier;
                        locked.AllowedPersonas = "[\"Judge\"]";
                        alternates.Add(locked);
                    }
                }

                int approved = 0;
                int refused = 0;
                foreach (string? preferredModel in new[] { null, "low", "mid", "high", "claude-opus-4-7", "gpt-5.6-sol", "example-model", "unrun-model" })
                {
                    Mission mission = MakeMission();
                    mission.PreferredModel = preferredModel;
                    foreach (Captain alternate in alternates)
                    {
                        bool couldSelect = LegacyCaptainSelector.CouldSelect(tiers, mission, alternate);
                        PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                            mission, refusing, Declared(), policyPresent: true, continuationAlreadyUsed: false,
                            new List<Captain> { refusing, alternate }, tiers);
                        bool continued = decision.Outcome == PolicyRefusalContinuationOutcomeEnum.Continue;
                        AssertEqual(couldSelect, continued,
                            "pin " + (preferredModel ?? "(none)") + ", captain " + alternate.Name + ": the continuation must approve exactly the captains assignment could choose");
                        if (continued) approved++; else refused++;
                    }
                }

                AssertTrue(approved > 0 && refused > 0, "the domain holds both approved and refused alternates");
            });

            await RunTest("HandleAsync records the refusal, continues once, then stops with the reason on a second refusal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    PolicyRefusalContinuationService service = new PolicyRefusalContinuationService(testDb.Driver, settings, logging);

                    Captain refusing = await testDb.Driver.Captains.CreateAsync(MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode));
                    Captain alternate = await testDb.Driver.Captains.CreateAsync(MakeCaptain("alternate", AgentRuntimeEnum.Codex));
                    Mission mission = MakeMission();
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.CaptainId = refusing.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission);

                    PolicyRefusalContinuationDecision first = await service.HandleAsync(mission, refusing, Declared(), policyPresent: true);
                    Mission? afterFirst = await testDb.Driver.Missions.ReadAsync(mission.Id);

                    AssertEqual(PolicyRefusalContinuationOutcomeEnum.Continue, first.Outcome, "the first refusal continues");
                    AssertEqual(MissionStatusEnum.Pending, afterFirst!.Status, "the continuation requeues the mission");
                    AssertNull(afterFirst.CaptainId, "the requeued mission is unbound from the refusing captain");
                    AssertTrue(PolicyRefusalContinuationService.IsContinuation(afterFirst), "the requeued mission is marked as a continuation");
                    AssertContains("inspecting the sample binary", afterFirst.FailureReason ?? "", "the refusal reason is preserved on the mission");
                    AssertTrue(MissionService.IsExcludedForAssignment(afterFirst, refusing), "assignment excludes the refusing captain");
                    AssertFalse(MissionService.IsExcludedForAssignment(afterFirst, alternate), "assignment allows the alternate-runtime captain");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 50);
                    AssertEqual(1, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.RefusalEventType), "the refusal is recorded");
                    AssertEqual(1, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.ContinuedEventType), "one continuation is recorded");
                    AssertContains("DeclaredRefusal", events.First(evt => evt.EventType == PolicyRefusalContinuationService.RefusalEventType).Payload ?? "", "the refusal kind is typed in the record");

                    afterFirst.Status = MissionStatusEnum.WorkProduced;
                    afterFirst.CaptainId = alternate.Id;
                    await testDb.Driver.Missions.UpdateAsync(afterFirst);

                    PolicyRefusalContinuationDecision second = await service.HandleAsync(afterFirst, alternate, Declared(), policyPresent: true);
                    Mission? afterSecond = await testDb.Driver.Missions.ReadAsync(mission.Id);

                    AssertEqual(PolicyRefusalContinuationOutcomeEnum.Stop, second.Outcome, "a second refusal stops");
                    AssertEqual(MissionStatusEnum.Failed, afterSecond!.Status, "the mission fails instead of retrying");
                    AssertContains(PolicyRefusalContinuationService.StoppedReasonPrefix, afterSecond.FailureReason ?? "", "the stop reason is recorded");

                    events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 50);
                    AssertEqual(2, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.RefusalEventType), "both refusals are recorded");
                    AssertEqual(1, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.ContinuedEventType), "there is never a second continuation");
                }
            });

            await RunTest("A provider safeguard block continues even when no owner policy was supplied", () =>
            {
                Captain refusing = MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode);
                List<Captain> captains = new List<Captain> { refusing, MakeCaptain("alternate", AgentRuntimeEnum.Codex) };
                CaptainRefusal block = CaptainRefusalClassifier.Classify("API Error: example-model has safety measures that flagged this message for a cybersecurity topic");

                PolicyRefusalContinuationDecision decision = PolicyRefusalContinuationService.Decide(
                    MakeMission(), refusing, block, policyPresent: false, continuationAlreadyUsed: false, captains, null);

                AssertEqual(PolicyRefusalContinuationOutcomeEnum.Continue, decision.Outcome, "the provider gate takes the one continuation without a policy");
            });

            await RunTest("A refusal driven through completion handling continues once and fails with the reason the second time", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_refusal_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_refusal_repos_" + Guid.NewGuid().ToString("N"));
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captainService, git: git);
                    missions.OnGetMissionOutput = _ => "Read the brief.\n" + CaptainRefusalClassifier.RefusalMarker + ": I will not inspect the sample binary";

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("ExampleVessel", "https://github.com/test/repo.git"));
                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "example-profile";
                    profile.Scope = ProjectProfileScopeEnum.Vessel;
                    profile.VesselId = vessel.Id;
                    profile.AuthorizationPolicy = "Interoperability work on the ExampleFormat sample binary is authorized.";
                    await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                    Captain refusing = await testDb.Driver.Captains.CreateAsync(MakeCaptain("refusing", AgentRuntimeEnum.ClaudeCode));
                    Captain alternate = await testDb.Driver.Captains.CreateAsync(MakeCaptain("alternate", AgentRuntimeEnum.Codex));

                    Mission mission = MakeMission();
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission);

                    // A refusal is a short run with no diff and no completion marker: the exact shape the no-op
                    // guard fails. The refusal must be classified first, or its reason is lost to that guard.
                    async Task RunOnAsync(Captain captain)
                    {
                        Mission current = (await testDb.Driver.Missions.ReadAsync(mission.Id))!;
                        current.Status = MissionStatusEnum.InProgress;
                        current.AssignmentState = MissionAssignmentStateEnum.Assigned;
                        current.CaptainId = captain.Id;
                        current.StartedUtc = DateTime.UtcNow.AddSeconds(-10);
                        await testDb.Driver.Missions.UpdateAsync(current);
                        captain.State = CaptainStateEnum.Working;
                        captain.CurrentMissionId = current.Id;
                        await testDb.Driver.Captains.UpdateAsync(captain);
                    }

                    await RunOnAsync(refusing);
                    await missions.HandleCompletionAsync(refusing, mission.Id);

                    Mission? afterFirst = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.Pending, afterFirst!.Status, "the first refusal requeues the mission");
                    AssertTrue(PolicyRefusalContinuationService.IsContinuation(afterFirst), "the mission waits as a continuation");
                    AssertFalse((afterFirst.FailureReason ?? "").Contains("no_op_completion_detected"), "the refusal is not recorded as a false complete");
                    AssertTrue(MissionService.IsExcludedForAssignment(afterFirst, refusing), "the refusing runtime is excluded");

                    // The alternate captain refuses within seconds of the first completion. Its completion must
                    // still be handled: a requeued mission is a new assignment, not a duplicate call.
                    await RunOnAsync(alternate);
                    await missions.HandleCompletionAsync(alternate, mission.Id);

                    Mission? afterSecond = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertEqual(MissionStatusEnum.Failed, afterSecond!.Status, "the second refusal fails the mission");
                    AssertTrue((afterSecond.FailureReason ?? "").StartsWith(PolicyRefusalContinuationService.StoppedReasonPrefix, StringComparison.Ordinal),
                        "the failure carries the refusal reason: " + afterSecond.FailureReason);
                    AssertContains("I will not inspect the sample binary", afterSecond.FailureReason ?? "", "the captain's own reason is preserved");
                    AssertFalse((afterSecond.FailureReason ?? "").Contains("no_op_completion_detected"), "the second refusal is not a false complete either");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 50);
                    AssertEqual(2, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.RefusalEventType), "both refusals are recorded");
                    AssertEqual(1, events.Count(evt => evt.EventType == PolicyRefusalContinuationService.ContinuedEventType), "exactly one continuation ran");
                }
            });

            await RunTest("Assignment exclusion applies only to a pending refusal continuation", () =>
            {
                Captain captain = MakeCaptain("any", AgentRuntimeEnum.ClaudeCode);
                Mission ordinary = MakeMission();
                ordinary.RetrySkipCaptainIds = captain.Id;
                AssertFalse(MissionService.IsExcludedForAssignment(ordinary, captain),
                    "an ordinary retry skip list keeps its fall-back-to-any-captain behaviour");
                AssertFalse(MissionService.IsExcludedForAssignment(null, captain), "a null mission excludes nobody");
            });
        }
    }
}
