namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using StubGitService = global::Test.Shared.Infrastructure.StubGitService;
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

    /// <summary>Tests public assignment behavior for versioned Architect integration barriers.</summary>
    public sealed class MissionServiceIntegrationBarrierTests : TestSuite
    {
        private const string TargetSha = "1111111111111111111111111111111111111111";
        private const string NewTargetSha = "3333333333333333333333333333333333333333";
        private const string SourceSha = "2222222222222222222222222222222222222222";

        /// <inheritdoc />
        public override string Name => "Mission Service Integration Barrier";

        protected override async Task RunTestsAsync()
        {
            await RunTest("PreparingBarrier_HoldsBeforeProvisioning", () => RunAssignmentCaseAsync(MissionStatusEnum.Complete, true, false, true)).ConfigureAwait(false);
            await RunTest("PendingMember_HoldsBeforeProvisioning", () => RunAssignmentCaseAsync(MissionStatusEnum.Pending, true, false)).ConfigureAwait(false);
            await RunTest("WorkProducedMember_HoldsBeforeProvisioning", () => RunAssignmentCaseAsync(MissionStatusEnum.WorkProduced, true, false)).ConfigureAwait(false);
            await RunTest("UnlandedMember_HoldsBeforeProvisioning", () => RunAssignmentCaseAsync(MissionStatusEnum.Complete, false, false)).ConfigureAwait(false);
            await RunTest("LandedMember_UsesFreshTargetForProvisioning", () => RunAssignmentCaseAsync(MissionStatusEnum.Complete, true, true)).ConfigureAwait(false);
            await RunTest("CaptainHarborOwnedRoute_HoldsAfterReadinessBeforeProvisioning", () => RunAssignmentCaseAsync(MissionStatusEnum.Complete, true, false, false, true)).ConfigureAwait(false);
            await RunTest("AcceptedRescue_IsRecordedAndWaitsForFreshTargetPass", () => RunRescueCaseAsync(false)).ConfigureAwait(false);
            await RunTest("CurrentRescue_RevalidatesEveryLineageOwner", () => RunRescueCaseAsync(true)).ConfigureAwait(false);
        }

        private async Task RunRescueCaseAsync(bool corruptAcceptedRescue)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                string key = Guid.NewGuid().ToString("N");
                ArmadaSettings settings = new ArmadaSettings();
                settings.ModelTier.CopyFrom(FleetRoutingSettings.CreateModelTier());
                settings.ModelTier.Records = FleetRoutingSettings.CreateModelTier().Records;
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada-barrier-rescue-" + key);
                settings.LandingMode = LandingModeEnum.LocalMerge;
                Vessel vessel = new Vessel("barrier-rescue-vessel-" + key, "https://github.com/test/barrier.git");
                vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                vessel.DefaultBranch = "main";
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("barrier rescue voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission failed = new Mission("Alpha [Worker]", "Failed branch.")
                {
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker",
                    Status = MissionStatusEnum.Failed, CommitHash = SourceSha
                };
                failed = await testDb.Driver.Missions.CreateAsync(failed).ConfigureAwait(false);
                Mission rescue = new Mission("Rescue Alpha [Worker]", RescueMissionMarker.Marker + "\nContinue the accepted branch.")
                {
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker",
                    Status = MissionStatusEnum.Complete, CommitHash = SourceSha,
                    ParentMissionId = failed.Id, DependsOnMissionId = failed.Id
                };
                rescue = await testDb.Driver.Missions.CreateAsync(rescue).ConfigureAwait(false);
                Mission integration = new Mission("Integrate [Worker]", "Combine branches.")
                {
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker",
                    Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForDependency,
                    IntegrationBarrier = new MissionIntegrationBarrier
                    {
                        Phase = MissionIntegrationBarrierPhaseEnum.Accepted,
                        ArchitectMissionId = "accepted-architect", PlanHash = "plan-hash",
                        IntegrationMissionId = "pending-id", VesselId = vessel.Id, VoyageId = voyage.Id,
                        OriginVoyageId = voyage.Id, ExpectedBranchCount = 1,
                        Members = new System.Collections.Generic.List<MissionIntegrationBarrierMember>
                        {
                            new MissionIntegrationBarrierMember { BranchKey = "M1", RootMissionId = failed.Id,
                                AcceptedTerminalMissionId = failed.Id, CurrentTerminalMissionId = failed.Id }
                        }
                    }
                };
                integration.IntegrationBarrier.IntegrationMissionId = integration.Id;
                integration = await testDb.Driver.Missions.CreateAsync(integration).ConfigureAwait(false);
                StubGitService git = new StubGitService { RevisionCommitShaResult = TargetSha };
                git.AncestorResults[SourceSha + "|" + NewTargetSha] = true;
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                DockService docks = new DockService(logging, testDb.Driver, settings, git);
                CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                captains.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
                Captain captain = new Captain("barrier-rescue-captain-" + key)
                { Model = "claude-opus-5", AllowedPersonas = "[\"Worker\"]", State = CaptainStateEnum.Idle };
                FleetRoutingSettings.ApplyTierTo(captain);
                await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains,
                    git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));

                AssertFalse(await missions.TryAssignAsync(integration, vessel).ConfigureAwait(false),
                    "recording a rescue must end this pass and resolve a fresh target on the next pass");
                Mission stored = (await testDb.Driver.Missions.ReadAsync(integration.Id).ConfigureAwait(false))!;
                AssertEqual(rescue.Id, stored.IntegrationBarrier!.Members[0].CurrentTerminalMissionId,
                    "the exact accepted branch rescue becomes the current terminal");
                AssertEqual("integration_barrier_waiting", stored.AssignmentHoldReason,
                    "the rescue CAS ends with the named wait for a fresh target pass");
                AssertEqual(0, git.WorktreeCalls.Count, "the rescue update does not provision against the old target snapshot");

                if (corruptAcceptedRescue)
                {
                    TenantMetadata foreignTenant = await testDb.Driver.Tenants.CreateAsync(
                        new TenantMetadata("barrier-cross-owner-" + key)).ConfigureAwait(false);
                    UserMaster foreignUser = await testDb.Driver.Users.CreateAsync(
                        new UserMaster(foreignTenant.Id, "barrier-cross-owner-" + key + "@example.test", "test-password")).ConfigureAwait(false);
                    Mission bridge = new Mission("Cross-owner bridge", "Invalid lineage hop.")
                    {
                        VesselId = vessel.Id, VoyageId = voyage.Id, TenantId = foreignTenant.Id, UserId = foreignUser.Id, Persona = "Worker",
                        Status = MissionStatusEnum.Complete, DependsOnMissionId = failed.Id
                    };
                    bridge = await testDb.Driver.Missions.CreateAsync(bridge).ConfigureAwait(false);
                    rescue.ParentMissionId = bridge.Id;
                    rescue.DependsOnMissionId = bridge.Id;
                    await testDb.Driver.Missions.UpdateAsync(rescue).ConfigureAwait(false);
                    AssertFalse(await missions.TryAssignAsync(stored, vessel).ConfigureAwait(false),
                        "a persisted current rescue must revalidate its full parent lineage");
                    Mission rejected = (await testDb.Driver.Missions.ReadAsync(integration.Id).ConfigureAwait(false))!;
                    AssertEqual("integration_barrier_unverifiable", rejected.AssignmentHoldReason,
                        "a cross-owner lineage hop receives an unverifiable hold");
                    AssertEqual(rescue.Id, rejected.AssignmentHoldEntityId,
                        "the bad current rescue is named in the hold");
                    AssertEqual(0, git.WorktreeCalls.Count, "invalid rescue lineage cannot start integration");
                    return;
                }

                git.RevisionCommitShas[vessel.LocalPath + "|refs/heads/main"] = NewTargetSha;
                AssertTrue(await missions.TryAssignAsync(stored, vessel).ConfigureAwait(false),
                    "the next pass proves the rescue against a newly resolved target and proceeds");
                AssertEqual(NewTargetSha, stored.StartFromRef, "the second pass uses the post-rescue target SHA");
            }
        }

        private async Task RunAssignmentCaseAsync(MissionStatusEnum memberStatus, bool landed, bool expectAssignment, bool preparing = false, bool harborOwnedCaptainRoute = false)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                string key = Guid.NewGuid().ToString("N");
                ArmadaSettings settings = new ArmadaSettings();
                settings.ModelTier.CopyFrom(FleetRoutingSettings.CreateModelTier());
                settings.ModelTier.Records = FleetRoutingSettings.CreateModelTier().Records;
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada-integration-barrier-" + key);
                settings.LandingMode = LandingModeEnum.LocalMerge;
                Vessel vessel = new Vessel("barrier-vessel-" + key, "https://github.com/test/barrier.git");
                vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
                vessel.DefaultBranch = "main";
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("barrier voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                Mission root = new Mission("Alpha [Worker]", "Branch output.")
                {
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Status = memberStatus,
                    CommitHash = SourceSha,
                    StartFromRef = "origin-anchor"
                };
                root = await testDb.Driver.Missions.CreateAsync(root).ConfigureAwait(false);

                Mission integration = new Mission("Integrate [Worker]", "Combine all branches.")
                {
                    BranchName = "stale-parallel-branch",
                    StartFromRef = "old-planner-ref"
                };
                MissionIntegrationBarrier integrationBarrier = new MissionIntegrationBarrier
                {
                    Phase = preparing ? MissionIntegrationBarrierPhaseEnum.Preparing : MissionIntegrationBarrierPhaseEnum.Accepted,
                    ArchitectMissionId = "architect-accepted",
                    PlanHash = "plan-hash",
                    IntegrationMissionId = integration.Id,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    OriginVoyageId = voyage.Id,
                    ExpectedBranchCount = 1,
                    Members = preparing
                        ? new System.Collections.Generic.List<MissionIntegrationBarrierMember>()
                        : new System.Collections.Generic.List<MissionIntegrationBarrierMember>
                    {
                        new MissionIntegrationBarrierMember
                        {
                            BranchKey = "M1",
                            RootMissionId = root.Id,
                            AcceptedTerminalMissionId = root.Id,
                            CurrentTerminalMissionId = root.Id
                        }
                    }
                };
                integration.VesselId = vessel.Id;
                integration.VoyageId = voyage.Id;
                integration.Persona = "Worker";
                integration.Status = MissionStatusEnum.Pending;
                integration.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                integration.IntegrationBarrier = integrationBarrier;
                integration = await testDb.Driver.Missions.CreateAsync(integration).ConfigureAwait(false);

                StubGitService git = new StubGitService { RevisionCommitShaResult = TargetSha };
                git.AncestorResults[SourceSha + "|" + TargetSha] = landed;
                git.AncestorResults["origin-anchor|" + TargetSha] = landed;
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                DockService docks = new DockService(logging, testDb.Driver, settings, git);
                CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                captains.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
                Captain captain = new Captain("barrier-captain-" + key)
                {
                    Model = "claude-opus-5",
                    AllowedPersonas = "[\"Worker\"]",
                    State = CaptainStateEnum.Idle
                };
                FleetRoutingSettings.ApplyTierTo(captain);
                await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                if (harborOwnedCaptainRoute)
                {
                    settings.Harbor.MissionRoutes = new System.Collections.Generic.List<HarborMissionRoute>
                    {
                        new HarborMissionRoute
                        {
                            CaptainId = captain.Id,
                            CheckoutMode = HarborCheckoutModeEnum.HarborOwned
                        }
                    };
                }
                MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains,
                    git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                Directory.CreateDirectory(Path.GetDirectoryName(vessel.LocalPath!)!);

                bool assigned = await missions.TryAssignAsync(integration, vessel).ConfigureAwait(false);
                AssertEqual(expectAssignment, assigned, "assignment result matches the branch completion and landing proof");
                if (expectAssignment)
                {
                    AssertEqual(TargetSha, integration.StartFromRef, "integration starts from the freshly resolved target tip");
                    AssertFalse(String.Equals("stale-parallel-branch", integration.BranchName, StringComparison.Ordinal),
                        "integration gets its own branch instead of reusing one branch's work");
                    AssertTrue(git.RevisionCommitShaCalls.Exists(call => call.EndsWith("|" + TargetSha, StringComparison.Ordinal)),
                        "dock preparation resolves the captured target SHA as its start point");
                    AssertTrue(git.WorktreeCalls.Count > 0, "a fully landed barrier reaches dock provisioning");
                }
                else
                {
                    AssertEqual(0, git.WorktreeCalls.Count, "an unready barrier never provisions a dock");
                    Mission held = (await testDb.Driver.Missions.ReadAsync(integration.Id).ConfigureAwait(false))!;
                    string expectedReason = preparing || !landed || harborOwnedCaptainRoute
                        ? "integration_barrier_unverifiable"
                        : "integration_barrier_waiting";
                    string expectedEntity = preparing ? integration.Id : harborOwnedCaptainRoute ? vessel.Id : root.Id;
                    AssertEqual(expectedReason, held.AssignmentHoldReason, "the barrier records the precise readiness hold");
                    AssertEqual(expectedEntity, held.AssignmentHoldEntityId, "the hold names the barrier or exact blocked branch");
                }
            }
        }
    }
}
