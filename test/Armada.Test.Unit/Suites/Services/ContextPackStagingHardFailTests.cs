namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Armada.Core.Enums;

    /// <summary>
    /// Tests that VoyageDispatchService hard-fails context-pack staging when
    /// CodeIndexSettings.RequireContextPackWhenEnabled is true and mode is auto or force,
    /// and proves the legacy warn-and-continue path survives when the flag is false.
    /// </summary>
    public class ContextPackStagingHardFailTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Context Pack Staging Hard-Fail";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("RequirePackEnabled_EmptyBuild_AutoMode_HardFailsWithActionableError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-1", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    // RequireContextPackWhenEnabled defaults to true

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Hard-fail voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task A", Description = "Implement feature A" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "dispatch must fail when pack is empty and RequireContextPackWhenEnabled=true");
                    AssertNotNull(result.Value, "result.Value (error payload) must not be null");
                    AssertFalse(admiral.DispatchVoyageCalled, "admiral must not be called when context-pack staging fails");
                }
            });

            await RunTest("RequirePackEnabled_ThrowingBuild_AutoMode_HardFailsWithActionableError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-2", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    ThrowingBuildCodeIndexService codeIndex = new ThrowingBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Throwing build voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task B", Description = "Implement feature B" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "dispatch must fail when build throws and RequireContextPackWhenEnabled=true");
                    AssertNotNull(result.Value, "result.Value (error payload) must not be null");
                    AssertFalse(admiral.DispatchVoyageCalled, "admiral must not be called when context-pack staging throws");
                }
            });

            await RunTest("RequirePackEnabled_SuccessfulBuild_AutoMode_SucceedsWithPrestagedFile", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-3", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    SuccessPackCodeIndexService codeIndex = new SuccessPackCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Success pack voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task C", Description = "Implement feature C" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "dispatch must succeed when pack build returns prestaged files");
                    AssertTrue(admiral.DispatchVoyageCalled, "admiral must be called on successful pack staging");
                    AssertEqual(1, admiral.CreatedMissions.Count, "one mission must be created");
                    List<PrestagedFile>? prestagedFiles = admiral.CreatedMissions[0].PrestagedFiles;
                    AssertNotNull(prestagedFiles, "created mission must have prestaged files");
                    bool hasContextPack = false;
                    foreach (PrestagedFile f in prestagedFiles!)
                    {
                        if (String.Equals(f.DestPath, "_briefing/context-pack.md", StringComparison.Ordinal))
                        {
                            hasContextPack = true;
                            break;
                        }
                    }
                    AssertTrue(hasContextPack, "created mission prestaged files must contain _briefing/context-pack.md");
                }
            });

            await RunTest("RequirePackDisabled_EmptyBuild_AutoMode_WarnsAndContinues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-4", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CodeIndex.RequireContextPackWhenEnabled = false;

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Legacy mode voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task D", Description = "Implement feature D" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "dispatch must succeed (warn-and-continue) when RequireContextPackWhenEnabled=false");
                    AssertTrue(admiral.DispatchVoyageCalled, "admiral must be called when legacy mode defers the pack build");
                }
            });

            await RunTest("OffMode_NoContextPackBuild_NoErrorRegardlessOfFlag", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-5", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    // RequireContextPackWhenEnabled=true but off mode must short-circuit before pack build

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Off mode voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task E", Description = "Implement feature E" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "off mode must always dispatch without error regardless of RequireContextPackWhenEnabled");
                    AssertEqual(0, codeIndex.BuildCallCount, "off mode must never call BuildContextPackAsync");
                }
            });

            await RunTest("ForceMode_EmptyBuild_FlagDisabled_StillHardFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-6", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    // Flag OFF must NOT relax force mode: force always hard-fails on an empty pack.
                    settings.CodeIndex.RequireContextPackWhenEnabled = false;

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Force empty voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "force",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task F", Description = "Implement feature F" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "force mode must hard-fail on an empty pack even when RequireContextPackWhenEnabled=false");
                    AssertNotNull(result.Value, "result.Value (error payload) must not be null");
                    AssertContains("Task F", result.Value.ToString() ?? "", "force-mode error must name the offending mission");
                    AssertFalse(admiral.DispatchVoyageCalled, "admiral must not be called when force-mode staging fails");
                    AssertEqual(1, codeIndex.BuildCallCount, "force mode must synchronously attempt the build (no deferral)");
                }
            });

            await RunTest("ForceMode_ThrowingBuild_FlagDisabled_StillHardFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-7", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    ThrowingBuildCodeIndexService codeIndex = new ThrowingBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CodeIndex.RequireContextPackWhenEnabled = false;

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Force throwing voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "force",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task G", Description = "Implement feature G" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "force mode must hard-fail when the build throws even when RequireContextPackWhenEnabled=false");
                    AssertNotNull(result.Value, "result.Value (error payload) must not be null");
                    AssertContains("Task G", result.Value.ToString() ?? "", "force-mode error must name the offending mission");
                    AssertFalse(admiral.DispatchVoyageCalled, "admiral must not be called when force-mode build throws");
                }
            });

            await RunTest("ForceMode_SuccessfulBuild_SucceedsWithPrestagedFile", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-8", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    SuccessPackCodeIndexService codeIndex = new SuccessPackCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Force success voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "force",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task H", Description = "Implement feature H" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "force mode must succeed when the build returns prestaged files");
                    AssertTrue(admiral.DispatchVoyageCalled, "admiral must be called on successful force-mode staging");
                    AssertEqual(1, admiral.CreatedMissions.Count, "one mission must be created");
                    List<PrestagedFile>? prestagedFiles = admiral.CreatedMissions[0].PrestagedFiles;
                    AssertNotNull(prestagedFiles, "created mission must have prestaged files");
                    bool hasContextPack = false;
                    foreach (PrestagedFile f in prestagedFiles!)
                    {
                        if (String.Equals(f.DestPath, "_briefing/context-pack.md", StringComparison.Ordinal))
                        {
                            hasContextPack = true;
                            break;
                        }
                    }
                    AssertTrue(hasContextPack, "force-mode created mission prestaged files must contain _briefing/context-pack.md");
                }
            });

            await RunTest("RequirePackEnabled_MultiMission_FirstFails_AbortsBeforeAnyDispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-9", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    // RequireContextPackWhenEnabled defaults to true

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Multi-mission abort voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task I-1", Description = "First mission" },
                            new MissionDescription { Title = "Task I-2", Description = "Second mission" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "dispatch must abort when the first mission's pack is empty");
                    AssertContains("Task I-1", result.Value.ToString() ?? "", "error must name the first failing mission");
                    AssertFalse(admiral.DispatchVoyageCalled, "no mission may dispatch when an earlier mission's staging fails");
                    AssertEqual(1, codeIndex.BuildCallCount, "the loop must short-circuit on first failure and never build the second mission's pack");
                }
            });

            await RunTest("RequirePackEnabled_EmptyQuery_AutoMode_HardFailsWithActionableError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-10", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    // RequireContextPackWhenEnabled defaults to true

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    // Mission with no title and no description yields an empty query from BuildMissionCodeContextQuery.
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Empty query voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "", Description = "" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "dispatch must fail when no query can be built and RequireContextPackWhenEnabled=true");
                    AssertNotNull(result.Value, "result.Value (error payload) must not be null");
                    AssertFalse(admiral.DispatchVoyageCalled, "admiral must not be called when query-build staging fails");
                    AssertEqual(0, codeIndex.BuildCallCount, "no build must be attempted when the query is empty");
                }
            });

            await RunTest("RequirePackEnabled_NullCodeIndexService_AutoMode_HardFails", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-11", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);

                    ArmadaSettings settings = new ArmadaSettings();
                    // RequireContextPackWhenEnabled defaults to true

                    // Construct with null code index service (4th parameter).
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, null, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Null service voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task J", Description = "Implement feature J" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "dispatch must fail when code index service is null and RequireContextPackWhenEnabled=true");
                    AssertNotNull(result.Value, "result.Value (error payload) must not be null");
                    AssertContains("Task J", result.Value.ToString() ?? "", "error must name the mission that triggered the failure");
                    AssertFalse(admiral.DispatchVoyageCalled, "admiral must not be called when service-unavailable staging fails");
                }
            });

            await RunTest("RequirePackDisabled_EmptyQuery_AutoMode_WarnsAndContinues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-12", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);
                    EmptyBuildCodeIndexService codeIndex = new EmptyBuildCodeIndexService();

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CodeIndex.RequireContextPackWhenEnabled = false;

                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, codeIndex, null, settings);

                    // Empty title and description produces an empty query; legacy path must warn and continue.
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Legacy empty-query voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "", Description = "" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "dispatch must succeed (warn-and-continue) when RequireContextPackWhenEnabled=false and query is empty");
                    AssertTrue(admiral.DispatchVoyageCalled, "admiral must be called on legacy warn-and-continue path");
                    AssertEqual(0, codeIndex.BuildCallCount, "no build must be attempted when query is empty");
                }
            });

            await RunTest("RequirePackDisabled_NullCodeIndexService_AutoMode_WarnsAndContinues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("hf-vessel-13", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    RecordingAdmiral admiral = new RecordingAdmiral(testDb.Driver);

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CodeIndex.RequireContextPackWhenEnabled = false;

                    // Construct with null code index service; legacy path must warn and continue.
                    VoyageDispatchService service = new VoyageDispatchService(
                        testDb.Driver, admiral, null, null, null, settings);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "Legacy null-service voyage",
                        Description = "test",
                        VesselId = vessel.Id,
                        CodeContextMode = "auto",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription { Title = "Task K", Description = "Implement feature K" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "dispatch must succeed (warn-and-continue) when RequireContextPackWhenEnabled=false and code index service is null");
                    AssertTrue(admiral.DispatchVoyageCalled, "admiral must be called on legacy warn-and-continue path");
                }
            });
        }

        #region Private-Types

        private sealed class RecordingAdmiral : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiral(DatabaseDriver database)
            {
                _Database = database ?? throw new ArgumentNullException(nameof(database));
            }

            public bool DispatchVoyageCalled { get; private set; }
            public List<Mission> CreatedMissions { get; } = new List<Mission>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, null, null, token).ConfigureAwait(false);

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, null, selectedPlaybooks, token).ConfigureAwait(false);

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, null, token).ConfigureAwait(false);

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                DispatchVoyageCalled = true;
                Voyage voyage = await _Database.Voyages.CreateAsync(
                    new Voyage(title, description)
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }, token).ConfigureAwait(false);

                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.TenantId = Constants.DefaultTenantId;
                    mission.UserId = Constants.DefaultUserId;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.PrestagedFiles = md.PrestagedFiles;
                    mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                    CreatedMissions.Add(mission);
                }

                return voyage;
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }

        private sealed class EmptyBuildCodeIndexService : ICodeIndexService
        {
            public int BuildCallCount { get; private set; }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                BuildCallCount++;
                return Task.FromResult(new ContextPackResponse());
            }

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);
        }

        private sealed class ThrowingBuildCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromException<ContextPackResponse>(new InvalidOperationException("simulated context pack build failure"));

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);
        }

        private sealed class SuccessPackCodeIndexService : ICodeIndexService
        {
            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                ContextPackResponse response = new ContextPackResponse();
                response.PrestagedFiles.Add(PrestagedFile.FromContent("_briefing/context-pack.md", "# Context Pack\n## Test content"));
                return Task.FromResult(response);
            }

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);
        }

        #endregion
    }
}
