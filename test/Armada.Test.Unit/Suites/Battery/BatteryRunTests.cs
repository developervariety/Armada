namespace Armada.Test.Unit.Suites.Battery
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// A battery run: it resets the fixture, dispatches each task through the dispatcher, grades what comes back, and
    /// stores one run record. A fake dispatcher stands in for real captains.
    /// </summary>
    public sealed class BatteryRunTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Battery Runs";

        private const string RunManifest = @"{
  ""schemaVersion"": 1, ""id"": ""mini"", ""name"": ""Mini"", ""version"": 3,
  ""fixture"": { ""path"": ""fixture"" },
  ""toolchain"": [
    { ""name"": ""git"", ""executable"": ""git"", ""arguments"": [ ""--version"" ] },
    { ""name"": ""ghost"", ""executable"": ""armada-battery-no-such-tool"", ""hint"": ""install ghost"" }
  ],
  ""tasks"": [
    { ""id"": ""passes"", ""title"": ""Passes"", ""prompt"": ""p1"", ""toolchain"": [ ""git"" ], ""grader"": { ""type"": ""command"", ""commands"": [ { ""executable"": ""git"", ""arguments"": [ ""grep"", ""--no-index"", ""-q"", ""done"", ""--"", ""result.txt"" ] } ] } },
    { ""id"": ""fails"", ""title"": ""Fails"", ""prompt"": ""p2"", ""toolchain"": [ ""git"" ], ""grader"": { ""type"": ""command"", ""commands"": [ { ""executable"": ""git"", ""arguments"": [ ""grep"", ""--no-index"", ""-q"", ""done"", ""--"", ""result.txt"" ] } ] } },
    { ""id"": ""other-captain"", ""title"": ""Other captain"", ""prompt"": ""p3"", ""toolchain"": [ ""git"" ], ""grader"": { ""type"": ""command"", ""commands"": [ { ""executable"": ""git"", ""arguments"": [ ""--version"" ] } ] } },
    { ""id"": ""needs-ghost"", ""title"": ""Needs ghost"", ""prompt"": ""p4"", ""toolchain"": [ ""ghost"" ], ""grader"": { ""type"": ""command"", ""commands"": [ { ""executable"": ""armada-battery-no-such-tool"" } ] } },
    { ""id"": ""also-needs-ghost"", ""title"": ""Also needs ghost"", ""prompt"": ""p5"", ""toolchain"": [ ""ghost"" ], ""grader"": { ""type"": ""command"", ""commands"": [ { ""executable"": ""armada-battery-no-such-tool"" } ] } }
  ]
}";

        /// <summary>
        /// Plays the captain: writes the worktree each task is graded in, and counts the dispatches.
        /// </summary>
        private sealed class FakeDispatcher : IBatteryTaskDispatcher
        {
            public List<string> Titles { get; } = new List<string>();

            public List<string> Directories { get; } = new List<string>();

            public TaskCompletionSource<bool>? Gate { get; set; } = null;

            public async Task<BatteryDispatchResult> RunAsync(BatteryDispatchRequest request, CancellationToken token = default)
            {
                Titles.Add(request.Title);
                if (Gate != null) await Gate.Task.ConfigureAwait(false);
                string directory = BatteryPackFixtures.NewDirectory("dock");
                Directories.Add(directory);
                File.WriteAllText(Path.Combine(directory, "result.txt"), request.Title == "Passes" ? "all done\n" : "not yet\n");
                Mission mission = new Mission(request.Title, request.Description)
                {
                    IsEvaluation = true,
                    CaptainId = request.Title == "Other captain" ? "cpt_someone_else" : request.Captain.Id,
                    Status = MissionStatusEnum.WorkProduced
                };
                return new BatteryDispatchResult { VoyageId = "vyg_fake", Mission = mission, Workspace = new LocalBatteryWorkspace(directory) };
            }
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Run_GradesEachTaskAndStoresOneRecord_WithCouldNotRunForUnjudgeableTasks", async () =>
            {
                string battery = BatteryPackFixtures.NewDirectory("battery");
                string root = BatteryPackFixtures.NewDirectory("fixtures");
                FakeDispatcher dispatcher = new FakeDispatcher();
                try
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    BatteryPackFixtures.WritePack(battery, "mini", RunManifest, new BatteryPackFixtures.PackFile("fixture/readme.txt", "fixture"));
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("battery-captain")).ConfigureAwait(false);
                    BatteryRunService service = NewService(testDb, battery, root, dispatcher);

                    BatteryRun queued = await service.StartAsync(new BatteryRunRequest { CaptainId = captain.Id, PackId = "mini", CreateFixtureVessel = true }, "usr_test").ConfigureAwait(false);
                    AssertEqual(BatteryRunStatusEnum.Running, queued.Status, "StartAsync returns before the tasks finish");
                    await service.WaitAsync(queued.Id).ConfigureAwait(false);

                    BatteryRun? stored = await testDb.Driver.BatteryRuns.ReadAsync(queued.Id).ConfigureAwait(false);
                    AssertNotNull(stored, "the run record is stored");
                    AssertEqual(BatteryRunStatusEnum.Completed, stored!.Status, stored.Error);
                    AssertEqual(captain.Id, stored.CaptainId);
                    AssertEqual("mini", stored.PackId);
                    AssertEqual(3, stored.PackVersion);
                    AssertNotNull(stored.CompletedUtc, "the end time is stored");
                    AssertEqual("passes,fails,other-captain,needs-ghost,also-needs-ghost", String.Join(",", stored.Results.Select(r => r.TaskId)));
                    AssertEqual("Passed,Failed,CouldNotRun,CouldNotRun,CouldNotRun", String.Join(",", stored.Results.Select(r => r.Grade)));
                    AssertEqual(1, stored.Passed);
                    AssertEqual(1, stored.Failed);
                    AssertEqual(3, stored.CouldNotRun);
                    AssertTrue(stored.Results.All(r => r.DurationMs >= 0 && r.StartedUtc <= r.CompletedUtc), "each task has timings");
                    AssertEqual("vyg_fake", stored.Results[0].VoyageId);
                    AssertContains("cpt_someone_else", stored.Results[2].Reason!, "a mission run by another captain is not graded");
                    AssertContains("install ghost", stored.Results[3].Reason!, "the missing tool is named with its hint");

                    AssertEqual("Passes,Fails,Other captain,Needs ghost", String.Join(",", dispatcher.Titles), "a tool found missing is not tried again by a later task");
                    AssertContains("Not dispatched", stored.Results[4].Reason!);

                    BatteryFixtureService fixtures = new BatteryFixtureService(testDb.Driver, new LoggingModule(), root);
                    Vessel? vessel = await fixtures.FindVesselAsync(BatteryPackLoader.LoadAll(battery).Single(), captain.TenantId).ConfigureAwait(false);
                    AssertNotNull(vessel, "the fixture vessel is created on request");
                    AssertEqual(LandingModeEnum.None, vessel!.LandingMode, "battery work is never landed");
                    AssertTrue(Directory.Exists(fixtures.OriginPathFor("mini")), "the fixture repository exists");

                    List<BatteryRun> byCaptain = await testDb.Driver.BatteryRuns.EnumerateAsync(captain.Id, null).ConfigureAwait(false);
                    AssertEqual(1, byCaptain.Count);
                    List<BatteryRun> otherPack = await testDb.Driver.BatteryRuns.EnumerateAsync(null, "elsewhere").ConfigureAwait(false);
                    AssertEqual(0, otherPack.Count, "the pack filter excludes the run");
                }
                finally
                {
                    foreach (string directory in dispatcher.Directories) Directory.Delete(directory, true);
                    Directory.Delete(battery, true);
                    Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("Run_StartIsRefused_ForUnknownInputsAndASecondRunOnTheSameCaptain", async () =>
            {
                string battery = BatteryPackFixtures.NewDirectory("battery");
                string root = BatteryPackFixtures.NewDirectory("fixtures");
                FakeDispatcher dispatcher = new FakeDispatcher { Gate = new TaskCompletionSource<bool>() };
                try
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    BatteryPackFixtures.WritePack(battery, "mini", RunManifest, new BatteryPackFixtures.PackFile("fixture/readme.txt", "fixture"));
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("battery-captain")).ConfigureAwait(false);
                    BatteryRunService service = NewService(testDb, battery, root, dispatcher);

                    await AssertThrowsAsync<ArgumentException>(() => service.StartAsync(new BatteryRunRequest { CaptainId = "cpt_missing", PackId = "mini" }, null), "unknown captain").ConfigureAwait(false);
                    await AssertThrowsAsync<ArgumentException>(() => service.StartAsync(new BatteryRunRequest { CaptainId = captain.Id, PackId = "nope" }, null), "unknown pack").ConfigureAwait(false);
                    await AssertThrowsAsync<ArgumentException>(() => service.StartAsync(new BatteryRunRequest { CaptainId = captain.Id, PackId = "mini" }, null), "no fixture vessel unless asked to create it").ConfigureAwait(false);
                    AssertEqual(0, (await testDb.Driver.BatteryRuns.EnumerateAsync(null, null).ConfigureAwait(false)).Count, "a refused start stores nothing");

                    BatteryRun first = await service.StartAsync(new BatteryRunRequest { CaptainId = captain.Id, PackId = "mini", CreateFixtureVessel = true }, null).ConfigureAwait(false);
                    await AssertThrowsAsync<InvalidOperationException>(() => service.StartAsync(new BatteryRunRequest { CaptainId = captain.Id, PackId = "mini" }, null), "one run per captain at a time").ConfigureAwait(false);

                    dispatcher.Gate.SetResult(true);
                    await service.WaitAsync(first.Id).ConfigureAwait(false);
                    BatteryRun second = await service.StartAsync(new BatteryRunRequest { CaptainId = captain.Id, PackId = "mini" }, null).ConfigureAwait(false);
                    await service.WaitAsync(second.Id).ConfigureAwait(false);
                    AssertEqual(2, (await testDb.Driver.BatteryRuns.EnumerateAsync(captain.Id, null).ConfigureAwait(false)).Count, "the captain can run again once the first run ends");
                }
                finally
                {
                    foreach (string directory in dispatcher.Directories) Directory.Delete(directory, true);
                    Directory.Delete(battery, true);
                    Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("Run_LeftRunningByAStoppedAdmiral_IsMarkedInterruptedAtStartup", async () =>
            {
                string battery = BatteryPackFixtures.NewDirectory("battery");
                string root = BatteryPackFixtures.NewDirectory("fixtures");
                try
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    BatteryRun orphan = await testDb.Driver.BatteryRuns.CreateAsync(new BatteryRun { CaptainId = "cpt_a", PackId = "mini" }).ConfigureAwait(false);
                    BatteryRun done = new BatteryRun { CaptainId = "cpt_a", PackId = "mini", Status = BatteryRunStatusEnum.Completed };
                    await testDb.Driver.BatteryRuns.CreateAsync(done).ConfigureAwait(false);

                    BatteryRunService service = NewService(testDb, battery, root, new FakeDispatcher());
                    AssertEqual(1, await service.RecoverInterruptedAsync().ConfigureAwait(false), "only the running run changes");

                    BatteryRun? read = await testDb.Driver.BatteryRuns.ReadAsync(orphan.Id).ConfigureAwait(false);
                    AssertEqual(BatteryRunStatusEnum.Interrupted, read!.Status);
                    AssertEqual(BatteryRunService.InterruptedReason, read.Error);
                    AssertNotNull(read.CompletedUtc, "the run has an end time");
                    AssertEqual(BatteryRunStatusEnum.Completed, (await testDb.Driver.BatteryRuns.ReadAsync(done.Id).ConfigureAwait(false))!.Status);
                }
                finally
                {
                    Directory.Delete(battery, true);
                    Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);

            await RunTest("McpTools_AnswerGlobalAdministratorsOnly_AndNoToolStartsARun", async () =>
            {
                string battery = BatteryPackFixtures.NewDirectory("battery");
                string root = BatteryPackFixtures.NewDirectory("fixtures");
                try
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    BatteryPackFixtures.WritePack(battery, "mini", RunManifest, new BatteryPackFixtures.PackFile("fixture/readme.txt", "fixture"));
                    BatteryRun run = await testDb.Driver.BatteryRuns.CreateAsync(new BatteryRun { CaptainId = "cpt_a", PackId = "mini" }).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpBatteryTools.Register((name, description, schema, handler) => handlers[name] = handler, NewService(testDb, battery, root, new FakeDispatcher()), testDb.Driver);
                    AssertEqual("armada_battery_packs,armada_battery_run,armada_battery_runs", String.Join(",", handlers.Keys.OrderBy(k => k, StringComparer.Ordinal)), "the tools read; none starts a run");

                    AuthContext admin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "test");
                    AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "test");
                    AuthContext mission = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "test");
                    mission.MissionId = "msn_example";

                    AssertContains(run.Id, JsonSerializer.Serialize(await CallAsync(handlers, McpBatteryTools.RunsToolName, admin, new { }).ConfigureAwait(false)), "an administrator lists runs");
                    AssertContains("Battery run not found", JsonSerializer.Serialize(await CallAsync(handlers, McpBatteryTools.RunToolName, admin, new { runId = "bat_missing" }).ConfigureAwait(false)));
                    foreach (string tool in handlers.Keys)
                    {
                        foreach (AuthContext caller in new[] { tenantAdmin, mission })
                        {
                            string answer = JsonSerializer.Serialize(await CallAsync(handlers, tool, caller, new { runId = run.Id }).ConfigureAwait(false));
                            AssertContains(McpBatteryTools.GlobalAdministratorRequiredReason, answer, tool + " refuses a caller that is not a global administrator");
                        }

                        AssertFalse(McpToolAccessPolicy.IsAllowed(mission, tool), tool + " is not offered to a mission credential");
                    }
                }
                finally
                {
                    Directory.Delete(battery, true);
                    Directory.Delete(root, true);
                }
            }).ConfigureAwait(false);
        }

        private static async Task<object> CallAsync(Dictionary<string, Func<JsonElement?, Task<object>>> handlers, string tool, AuthContext caller, object arguments)
        {
            using (McpCallerContext.Begin(caller))
            {
                return await handlers[tool](JsonSerializer.SerializeToElement(arguments)).ConfigureAwait(false);
            }
        }

        private static BatteryRunService NewService(TestDatabase testDb, string battery, string root, IBatteryTaskDispatcher dispatcher)
        {
            LoggingModule logging = new LoggingModule();
            return new BatteryRunService(testDb.Driver, logging, new BatteryPackCatalog(() => battery), new BatteryFixtureService(testDb.Driver, logging, root), dispatcher);
        }
    }
}
