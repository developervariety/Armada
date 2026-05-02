namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Regression tests for voyage-level merged playbooks propagating to persisted
    /// mission snapshots (and per-mission selection merge).
    /// </summary>
    public class VoyageMissionPlaybookPropagationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Voyage mission playbook propagation";

        /// <summary>Run tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Dispatch_VesselDefaults_MaterializeSnapshotsOnEachMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = CreateSettings();

                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselWithTenantAsync(testDb).ConfigureAwait(false);

                    Playbook pb1 = await SeedPlaybookAsync(testDb, "snap-a").ConfigureAwait(false);
                    Playbook pb2 = await SeedPlaybookAsync(testDb, "snap-b").ConfigureAwait(false);
                    Playbook pb3 = await SeedPlaybookAsync(testDb, "snap-c").ConfigureAwait(false);
                    Playbook pb4 = await SeedPlaybookAsync(testDb, "snap-d").ConfigureAwait(false);

                    List<object> defaults = new List<object>();
                    defaults.Add(new { playbookId = pb1.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb2.Id, deliveryMode = "InstructionWithReference" });
                    defaults.Add(new { playbookId = pb3.Id, deliveryMode = "AttachIntoWorktree" });
                    defaults.Add(new { playbookId = pb4.Id, deliveryMode = "InlineFullContent" });

                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    List<SelectedPlaybook> voyagePlaybooks =
                        PlaybookMerge.MergeWithVesselDefaults(vessel.GetDefaultPlaybooks(), new List<SelectedPlaybook>());

                    List<MissionDescription> missionDescriptions = new List<MissionDescription>
                    {
                        new MissionDescription("Alpha", "desc a"),
                        new MissionDescription("Beta", "desc b")
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Pb voyage",
                        "playbook propagation",
                        vessel.Id,
                        missionDescriptions,
                        voyagePlaybooks).ConfigureAwait(false);

                    List<Mission> voyMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, voyMissions.Count, "Expected two missions");

                    foreach (Mission mission in voyMissions)
                    {
                        List<MissionPlaybookSnapshot> snaps = await testDb.Driver.Playbooks
                            .GetMissionSnapshotsAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(4, snaps.Count, "Each mission must receive four playbook snapshots");

                        Dictionary<string, PlaybookDeliveryModeEnum> pbToMode =
                            new Dictionary<string, PlaybookDeliveryModeEnum>
                            {
                                { pb1.Id, PlaybookDeliveryModeEnum.InlineFullContent },
                                { pb2.Id, PlaybookDeliveryModeEnum.InstructionWithReference },
                                { pb3.Id, PlaybookDeliveryModeEnum.AttachIntoWorktree },
                                { pb4.Id, PlaybookDeliveryModeEnum.InlineFullContent }
                            };

                        foreach (MissionPlaybookSnapshot snap in snaps)
                        {
                            AssertTrue(
                                pbToMode.TryGetValue(snap.PlaybookId ?? "", out PlaybookDeliveryModeEnum expectedMode),
                                "snapshot playbook id unknown: " + snap.PlaybookId);
                            AssertEqual(expectedMode, snap.DeliveryMode, "Delivery mode mismatch for playbook " + snap.PlaybookId);
                        }
                    }
                }
            });

            await RunTest("Dispatch_PerMissionSelectionsMergeAgainstVoyageList", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = CreateSettings();

                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselWithTenantAsync(testDb).ConfigureAwait(false);

                    Playbook pb1 = await SeedPlaybookAsync(testDb, "merge-a").ConfigureAwait(false);
                    Playbook pb2 = await SeedPlaybookAsync(testDb, "merge-b").ConfigureAwait(false);

                    List<MissionDescription> missionDescriptions = new List<MissionDescription>
                    {
                        new MissionDescription("Only voy", "plain")
                        {
                            SelectedPlaybooks = new List<SelectedPlaybook>()
                        },
                        new MissionDescription("Overrides first", "with per-mission")
                        {
                            SelectedPlaybooks = new List<SelectedPlaybook>
                            {
                                new SelectedPlaybook
                                {
                                    PlaybookId = pb1.Id,
                                    DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree
                                }
                            }
                        }
                    };

                    List<SelectedPlaybook> voyagePlaybooks = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook
                        {
                            PlaybookId = pb1.Id,
                            DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent
                        },
                        new SelectedPlaybook
                        {
                            PlaybookId = pb2.Id,
                            DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference
                        }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Pb merge voyage",
                        "merge",
                        vessel.Id,
                        missionDescriptions,
                        voyagePlaybooks).ConfigureAwait(false);

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);

                    Mission? first = missions.Find(mission => mission.Title == "Only voy");
                    Mission? second = missions.Find(mission => mission.Title == "Overrides first");
                    AssertNotNull(first, "first mission");
                    AssertNotNull(second, "second mission");

                    List<MissionPlaybookSnapshot> firstSnaps = await testDb.Driver.Playbooks.GetMissionSnapshotsAsync(first!.Id).ConfigureAwait(false);
                    MissionPlaybookSnapshot? firstPb1 =
                        firstSnaps.Find(snap => snap.PlaybookId == pb1.Id);
                    AssertNotNull(firstPb1, "snapshot for pb1 on first mission");
                    AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, firstPb1!.DeliveryMode,
                        "first mission inherits voyage InlineFullContent");

                    List<MissionPlaybookSnapshot> secondSnaps = await testDb.Driver.Playbooks.GetMissionSnapshotsAsync(second!.Id).ConfigureAwait(false);
                    MissionPlaybookSnapshot? secondPb1 =
                        secondSnaps.Find(snap => snap.PlaybookId == pb1.Id);
                    AssertNotNull(secondPb1, "snapshot for pb1 on second mission");
                    AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, secondPb1!.DeliveryMode,
                        "second mission overrides delivery mode via per-mission list");
                }
            });

            await RunTest("Dispatch_VesselDefaultsPlusVoyagePlaybook_MissionSnapshotHasFiveEntries", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = CreateSettings();

                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselWithTenantAsync(testDb).ConfigureAwait(false);

                    Playbook pb1 = await SeedPlaybookAsync(testDb, "voy5-a").ConfigureAwait(false);
                    Playbook pb2 = await SeedPlaybookAsync(testDb, "voy5-b").ConfigureAwait(false);
                    Playbook pb3 = await SeedPlaybookAsync(testDb, "voy5-c").ConfigureAwait(false);
                    Playbook pb4 = await SeedPlaybookAsync(testDb, "voy5-d").ConfigureAwait(false);
                    Playbook pb5 = await SeedPlaybookAsync(testDb, "voy5-e").ConfigureAwait(false);

                    List<object> defaults = new List<object>();
                    defaults.Add(new { playbookId = pb1.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb2.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb3.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb4.Id, deliveryMode = "InlineFullContent" });

                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    List<SelectedPlaybook> voyageExtra = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = pb5.Id, DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference }
                    };
                    List<SelectedPlaybook> voyagePlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                        vessel.GetDefaultPlaybooks(), voyageExtra);

                    List<MissionDescription> missionDescriptions = new List<MissionDescription>
                    {
                        new MissionDescription("Alpha", "desc a"),
                        new MissionDescription("Beta", "desc b")
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Voy plus extra",
                        "vessel defaults plus voyage extra",
                        vessel.Id,
                        missionDescriptions,
                        voyagePlaybooks).ConfigureAwait(false);

                    List<Mission> voyMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, voyMissions.Count, "Expected two missions");

                    foreach (Mission mission in voyMissions)
                    {
                        List<MissionPlaybookSnapshot> snaps = await testDb.Driver.Playbooks
                            .GetMissionSnapshotsAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(5, snaps.Count, "Mission must have 5 snapshots (4 vessel defaults + 1 voyage extra)");

                        MissionPlaybookSnapshot? pb5Snap = snaps.Find(s => s.PlaybookId == pb5.Id);
                        AssertNotNull(pb5Snap, "voyage-extra pb5 snapshot must be present on " + mission.Title);
                        AssertEqual(
                            PlaybookDeliveryModeEnum.InstructionWithReference,
                            pb5Snap!.DeliveryMode,
                            "pb5 must carry the voyage-level delivery mode on " + mission.Title);
                    }
                }
            });

            await RunTest("Dispatch_VoyageExtraWithPerMissionOverride_PerMissionDeliveryModeWins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = CreateSettings();

                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselWithTenantAsync(testDb).ConfigureAwait(false);

                    Playbook pb1 = await SeedPlaybookAsync(testDb, "ovr-a").ConfigureAwait(false);
                    Playbook pb2 = await SeedPlaybookAsync(testDb, "ovr-b").ConfigureAwait(false);
                    Playbook pb3 = await SeedPlaybookAsync(testDb, "ovr-c").ConfigureAwait(false);
                    Playbook pb4 = await SeedPlaybookAsync(testDb, "ovr-d").ConfigureAwait(false);
                    Playbook pb5 = await SeedPlaybookAsync(testDb, "ovr-e").ConfigureAwait(false);

                    List<object> defaults = new List<object>();
                    defaults.Add(new { playbookId = pb1.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb2.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb3.Id, deliveryMode = "InlineFullContent" });
                    defaults.Add(new { playbookId = pb4.Id, deliveryMode = "InlineFullContent" });

                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    List<SelectedPlaybook> voyageExtra = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = pb5.Id, DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                    };
                    List<SelectedPlaybook> voyagePlaybooks = PlaybookMerge.MergeWithVesselDefaults(
                        vessel.GetDefaultPlaybooks(), voyageExtra);

                    List<MissionDescription> missionDescriptions = new List<MissionDescription>
                    {
                        new MissionDescription("Worker", "with per-mission override")
                        {
                            SelectedPlaybooks = new List<SelectedPlaybook>
                            {
                                new SelectedPlaybook
                                {
                                    PlaybookId = pb5.Id,
                                    DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree
                                }
                            }
                        }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Ovr voyage",
                        "per-mission overrides voyage extra",
                        vessel.Id,
                        missionDescriptions,
                        voyagePlaybooks).ConfigureAwait(false);

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(1, missions.Count, "Expected one mission");

                    List<MissionPlaybookSnapshot> snaps = await testDb.Driver.Playbooks
                        .GetMissionSnapshotsAsync(missions[0].Id).ConfigureAwait(false);
                    AssertEqual(5, snaps.Count, "Mission must have 5 snapshots (4 vessel defaults + 1 voyage extra, no duplicate)");

                    MissionPlaybookSnapshot? pb5Snap = snaps.Find(s => s.PlaybookId == pb5.Id);
                    AssertNotNull(pb5Snap, "pb5 snapshot must be present");
                    AssertEqual(
                        PlaybookDeliveryModeEnum.AttachIntoWorktree,
                        pb5Snap!.DeliveryMode,
                        "per-mission delivery mode must override voyage delivery mode");
                }
            });
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + System.Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + System.Guid.NewGuid().ToString("N"));
            return settings;
        }

        private AdmiralService BuildAdmiral(TestDatabase testDb, LoggingModule logging, ArmadaSettings settings)
        {
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
            IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
            return new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
        }

        private static async Task<Vessel> CreateVesselWithTenantAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("pb-propagation-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + System.Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + System.Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel.TenantId = Constants.DefaultTenantId;
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Playbook> SeedPlaybookAsync(TestDatabase testDb, string fileStem)
        {
            Playbook playbook = new Playbook(fileStem + ".md", "# playbook " + fileStem);
            playbook.TenantId = Constants.DefaultTenantId;
            return await testDb.Driver.Playbooks.CreateAsync(playbook).ConfigureAwait(false);
        }
    }
}
