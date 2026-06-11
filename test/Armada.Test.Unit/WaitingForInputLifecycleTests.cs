namespace Armada.Test.Unit
{
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Lifecycle tests for the WaitingForInput mission status beyond enum identity:
    /// dependency cancellation, voyage terminal-status calculations, review-decision
    /// rejection, SQLite update-path persistence, escalation trigger serialization,
    /// and source guards for the private transition validators and Helm rendering
    /// fallbacks that cannot be exercised directly from this test project.
    /// </summary>
    public sealed class WaitingForInputLifecycleTests : TestSuite
    {
        public override string Name => "Mission WaitingForInput Lifecycle";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private IMissionService CreateMissionService(TestDatabase testDb)
        {
            StubGitService git = new StubGitService();
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();

            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            return new MissionService(logging, testDb.Driver, settings, dockService, captainService);
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src"))
                    && Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private static string ReadSource(string relativePath)
        {
            string fullPath = Path.Combine(FindRepositoryRoot(), relativePath);
            return File.ReadAllText(fullPath);
        }

        protected override async Task RunTestsAsync()
        {
            // === Dependency cancellation (MissionService.CancelDependentPipelineStagesAsync) ===

            await RunTest("DenyReview FailPipeline cancels WaitingForInput dependent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    IMissionService missionService = CreateMissionService(testDb);

                    Voyage voyage = new Voyage("wfi-cancel-voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Mission upstream = new Mission("upstream-in-review");
                    upstream.VoyageId = voyage.Id;
                    upstream.Status = MissionStatusEnum.Review;
                    upstream.RequiresReview = true;
                    upstream.ReviewDenyAction = ReviewDenyActionEnum.FailPipeline;
                    await testDb.Driver.Missions.CreateAsync(upstream);

                    Mission dependent = new Mission("dependent-waiting-for-input");
                    dependent.VoyageId = voyage.Id;
                    dependent.DependsOnMissionId = upstream.Id;
                    dependent.Status = MissionStatusEnum.WaitingForInput;
                    await testDb.Driver.Missions.CreateAsync(dependent);

                    Mission denied = await missionService.DenyReviewAsync(upstream.Id, "tester", "fail the pipeline");
                    AssertEqual(MissionStatusEnum.Failed, denied.Status, "Upstream mission should be Failed");

                    Mission? cancelledDependent = await testDb.Driver.Missions.ReadAsync(dependent.Id);
                    AssertNotNull(cancelledDependent, "Dependent mission should still exist");
                    AssertEqual(MissionStatusEnum.Cancelled, cancelledDependent!.Status, "WaitingForInput dependent should be Cancelled");
                    AssertContains("Blocked by failed dependency", cancelledDependent.FailureReason ?? "", "Cancellation reason");
                }
            });

            await RunTest("Voyage reaches Failed after WaitingForInput dependent is cancelled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    IMissionService missionService = CreateMissionService(testDb);

                    Voyage voyage = new Voyage("wfi-terminal-voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Mission upstream = new Mission("upstream-in-review");
                    upstream.VoyageId = voyage.Id;
                    upstream.Status = MissionStatusEnum.Review;
                    upstream.RequiresReview = true;
                    upstream.ReviewDenyAction = ReviewDenyActionEnum.FailPipeline;
                    await testDb.Driver.Missions.CreateAsync(upstream);

                    Mission dependent = new Mission("dependent-waiting-for-input");
                    dependent.VoyageId = voyage.Id;
                    dependent.DependsOnMissionId = upstream.Id;
                    dependent.Status = MissionStatusEnum.WaitingForInput;
                    await testDb.Driver.Missions.CreateAsync(dependent);

                    await missionService.DenyReviewAsync(upstream.Id, "tester", "fail the pipeline");

                    Voyage? updatedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(updatedVoyage, "Voyage should still exist");
                    AssertEqual(VoyageStatusEnum.Failed, updatedVoyage!.Status, "Voyage should be terminal Failed once no mission is active");
                }
            });

            // === Voyage non-terminal (MissionService.UpdateVoyageTerminalStatusAsync) ===

            await RunTest("WaitingForInput sibling keeps voyage non-terminal after pipeline failure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    IMissionService missionService = CreateMissionService(testDb);

                    Voyage voyage = new Voyage("wfi-active-voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Mission failing = new Mission("failing-mission");
                    failing.VoyageId = voyage.Id;
                    failing.Status = MissionStatusEnum.Review;
                    failing.RequiresReview = true;
                    failing.ReviewDenyAction = ReviewDenyActionEnum.FailPipeline;
                    await testDb.Driver.Missions.CreateAsync(failing);

                    // Sibling does NOT depend on the failing mission, so it must survive
                    // the deny and keep the voyage non-terminal.
                    Mission sibling = new Mission("independent-waiting-for-input");
                    sibling.VoyageId = voyage.Id;
                    sibling.Status = MissionStatusEnum.WaitingForInput;
                    await testDb.Driver.Missions.CreateAsync(sibling);

                    await missionService.DenyReviewAsync(failing.Id, "tester", "fail the pipeline");

                    Mission? survivingSibling = await testDb.Driver.Missions.ReadAsync(sibling.Id);
                    AssertNotNull(survivingSibling, "Sibling mission should still exist");
                    AssertEqual(MissionStatusEnum.WaitingForInput, survivingSibling!.Status, "Non-dependent sibling must stay WaitingForInput");

                    Voyage? updatedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(updatedVoyage, "Voyage should still exist");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Voyage must stay non-terminal while a mission is WaitingForInput");
                }
            });

            await RunTest("VoyageService CheckCompletions leaves WaitingForInput voyage active", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);

                    Voyage voyage = new Voyage("wfi-check-completions");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await testDb.Driver.Voyages.CreateAsync(voyage);

                    Mission waiting = new Mission("waiting-mission");
                    waiting.VoyageId = voyage.Id;
                    waiting.Status = MissionStatusEnum.WaitingForInput;
                    await testDb.Driver.Missions.CreateAsync(waiting);

                    List<Voyage> completed = await voyageService.CheckCompletionsAsync();
                    AssertFalse(completed.Any(v => v.Id == voyage.Id), "WaitingForInput voyage must not be reported as completed");

                    Voyage? updatedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(updatedVoyage, "Voyage should still exist");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Voyage status must remain InProgress");
                }
            });

            // === Review-decision surfaces reject WaitingForInput (not confused with Review) ===

            await RunTest("ApproveReview rejects mission in WaitingForInput", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    IMissionService missionService = CreateMissionService(testDb);

                    Mission waiting = new Mission("waiting-not-reviewable");
                    waiting.Status = MissionStatusEnum.WaitingForInput;
                    waiting.RequiresReview = true;
                    await testDb.Driver.Missions.CreateAsync(waiting);

                    await AssertThrowsAsync<InvalidOperationException>(async () =>
                    {
                        await missionService.ApproveReviewAsync(waiting.Id, "tester");
                    }, "WaitingForInput must not be approvable as a review");
                }
            });

            await RunTest("DenyReview rejects mission in WaitingForInput", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    IMissionService missionService = CreateMissionService(testDb);

                    Mission waiting = new Mission("waiting-not-deniable");
                    waiting.Status = MissionStatusEnum.WaitingForInput;
                    waiting.RequiresReview = true;
                    await testDb.Driver.Missions.CreateAsync(waiting);

                    await AssertThrowsAsync<InvalidOperationException>(async () =>
                    {
                        await missionService.DenyReviewAsync(waiting.Id, "tester");
                    }, "WaitingForInput must not be deniable as a review");
                }
            });

            // === SQLite UPDATE path (Worker covered the INSERT path) ===

            await RunTest("SQLite update path round-trips WaitingForInput", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Mission mission = new Mission("update-path-mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    await testDb.Driver.Missions.CreateAsync(mission);

                    mission.Status = MissionStatusEnum.WaitingForInput;
                    await testDb.Driver.Missions.UpdateAsync(mission);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertNotNull(readBack, "Mission should exist after update");
                    AssertEqual(MissionStatusEnum.WaitingForInput, readBack!.Status, "Updated status");
                }
            });

            // === EscalationTriggerEnum JSON round-trip ===

            await RunTest("MissionAwaitingInput serializes as string", () =>
            {
                string json = JsonSerializer.Serialize(EscalationTriggerEnum.MissionAwaitingInput);
                AssertEqual("\"MissionAwaitingInput\"", json, "Serialized JSON");
                EscalationTriggerEnum deserialized = JsonSerializer.Deserialize<EscalationTriggerEnum>(json);
                AssertEqual(EscalationTriggerEnum.MissionAwaitingInput, deserialized, "Round-tripped value");
            });

            // === Source guards: private validators and Helm rendering ===
            // AgentLifecycleHandler.IsValidTransition, WebSocketCommandHandler.IsValidTransition,
            // and MissionRoutes.IsValidTransition are private, and Armada.Helm is not referenced
            // by this test project, so these surfaces are pinned with source guards (the accepted
            // pattern for this vessel when runtime coverage is impractical).

            await RunTest("AgentLifecycleHandler validator includes WaitingForInput arms", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "AgentLifecycleHandler.cs"));
                AssertContains("(MissionStatusEnum.InProgress, MissionStatusEnum.WaitingForInput) => true", contents, "InProgress -> WaitingForInput arm");
                AssertContains("(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Pending) => true", contents, "WaitingForInput -> Pending arm");
                AssertContains("(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Failed) => true", contents, "WaitingForInput -> Failed arm");
                AssertContains("(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Cancelled) => true", contents, "WaitingForInput -> Cancelled arm");
            });

            await RunTest("WebSocketCommandHandler validator includes WaitingForInput arms", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "WebSocket", "WebSocketCommandHandler.cs"));
                AssertContains("(MissionStatusEnum.InProgress, MissionStatusEnum.WaitingForInput) => true", contents, "InProgress -> WaitingForInput arm");
                AssertContains("(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Pending) => true", contents, "WaitingForInput -> Pending arm");
                AssertContains("(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Failed) => true", contents, "WaitingForInput -> Failed arm");
                AssertContains("(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Cancelled) => true", contents, "WaitingForInput -> Cancelled arm");
            });

            await RunTest("MissionRoutes validator includes WaitingForInput arms", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "Routes", "MissionRoutes.cs"));
                string normalized = Regex.Replace(contents, @"\s+", " ");
                AssertContains("|| target == MissionStatusEnum.WaitingForInput", normalized, "InProgress allows WaitingForInput target");
                AssertContains(
                    "if (current == MissionStatusEnum.WaitingForInput) { return target == MissionStatusEnum.Pending || target == MissionStatusEnum.Failed || target == MissionStatusEnum.Cancelled; }",
                    normalized,
                    "WaitingForInput outbound transitions");
            });

            await RunTest("TableRenderer maps WaitingForInput to ASCII-safe color and icon", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Helm", "Rendering", "TableRenderer.cs"));
                AssertContains("\"WaitingForInput\" => \"yellow\"", contents, "Color mapping");
                AssertContains("\"WaitingForInput\" => \"[?]\"", contents, "ASCII icon mapping");
            });
        }
    }
}
