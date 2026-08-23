namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the fleet-wide dispatch hold: state transitions, the admiral
    /// dispatch guard, and the armada_dispatch_hold tool including its
    /// coordination-board announcements.
    /// </summary>
    public class DispatchHoldTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public override string Name => "DispatchHold";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Engage snapshot and clear round-trips", () =>
            {
                DispatchHold hold = new DispatchHold();
                AssertNull(hold.Snapshot(), "a fresh hold must not be active");

                hold.Engage("rebuilding admiral", "session-a");
                DispatchHoldSnapshot? snap = hold.Snapshot();
                AssertNotNull(snap);
                AssertEqual("rebuilding admiral", snap!.Reason);
                AssertEqual("session-a", snap.SetBy);

                hold.Clear();
                AssertNull(hold.Snapshot(), "a cleared hold must not be active");
                return Task.CompletedTask;
            });

            await RunTest("ThrowIfActive names the holder reason and recovery action", () =>
            {
                DispatchHold hold = new DispatchHold();
                hold.ThrowIfActive();

                hold.Engage("schema migration", "session-b");
                Exception? captured = Capture(() => hold.ThrowIfActive());
                AssertNotNull(captured, "an active hold must refuse dispatch");
                AssertTrue(captured is InvalidOperationException, "the refusal must be an InvalidOperationException");
                AssertContains("Dispatch hold active", captured!.Message);
                AssertContains("schema migration", captured.Message);
                AssertContains("session-b", captured.Message);
                AssertContains("armada_dispatch_hold", captured.Message);

                hold.Clear();
                hold.ThrowIfActive();
                return Task.CompletedTask;
            });

            await RunTest("Engage requires a reason and a named session", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    (Func<JsonElement?, Task<object>> handler, _, _) = BuildToolHarness(testDb);

                    JsonElement missingReason = JsonSerializer.SerializeToElement(new { action = "engage", setBy = "s" }, _JsonOpts);
                    object result = await handler(missingReason).ConfigureAwait(false);
                    AssertContains("reason is required", JsonSerializer.Serialize(result));

                    JsonElement missingSetBy = JsonSerializer.SerializeToElement(new { action = "engage", reason = "r" }, _JsonOpts);
                    result = await handler(missingSetBy).ConfigureAwait(false);
                    AssertContains("setBy is required", JsonSerializer.Serialize(result));
                }
            });

            await RunTest("Engage announces on the board and status reports the hold until cleared", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    (Func<JsonElement?, Task<object>> handler, CoordinationService coordination, DispatchHold hold) = BuildToolHarness(testDb);

                    JsonElement engage = JsonSerializer.SerializeToElement(new { action = "engage", reason = "redeploy", setBy = "session-a" }, _JsonOpts);
                    object result = await handler(engage).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("\"Active\":true", resultJson);

                    var messages = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey);
                    AssertEqual(1, messages.Count);
                    AssertContains("[hold]", messages[0].Content);
                    AssertContains("redeploy", messages[0].Content);
                    AssertContains("session-a", messages[0].Content);

                    JsonElement status = JsonSerializer.SerializeToElement(new { action = "status" }, _JsonOpts);
                    result = await handler(status).ConfigureAwait(false);
                    AssertContains("\"Active\":true", JsonSerializer.Serialize(result));
                    AssertNotNull(hold.Snapshot());

                    JsonElement clear = JsonSerializer.SerializeToElement(new { action = "clear" }, _JsonOpts);
                    await handler(clear).ConfigureAwait(false);

                    var after = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey);
                    AssertEqual(2, after.Count);
                    AssertContains("resumed", after[1].Content);
                    AssertNull(hold.Snapshot());
                }
            });

            await RunTest("AdmiralService refuses dispatches while held and resumes after clear", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DispatchHold hold = new DispatchHold();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, hold);
                    hold.Engage("probe redeploy", "session-hold");

                    Mission mission = new Mission { Title = "hold-probe", VesselId = "vsl_example" };
                    Exception? refused = await CaptureAsync(() => admiral.DispatchMissionAsync(mission));
                    AssertNotNull(refused, "a held admiral must refuse the dispatch");
                    AssertContains("Dispatch hold active", refused!.Message);

                    hold.Clear();

                    Vessel vessel = new Vessel { Name = "example-vessel", RepoUrl = "https://git.example.com/example.git" };
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    Mission mission2 = new Mission { Title = "hold-probe-2", VesselId = vessel.Id };
                    Mission dispatched = await admiral.DispatchMissionAsync(mission2);
                    AssertNotNull(dispatched);
                    AssertStartsWith("msn_", dispatched.Id);
                }
            });
        }

        private static Exception? Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static async Task<Exception?> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private (Func<JsonElement?, Task<object>> Handler, CoordinationService Coordination, DispatchHold Hold) BuildToolHarness(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            DispatchHold hold = new DispatchHold();
            CoordinationService coordination = new CoordinationService(logging, testDb.Driver);

            Func<JsonElement?, Task<object>>? handler = null;
            McpCoordinationTools.Register(
                (name, _, _, h) => { if (name == "armada_dispatch_hold") handler = h; },
                testDb.Driver,
                coordination,
                hold);
            AssertNotNull(handler, "armada_dispatch_hold handler must be registered");
            return (handler!, coordination, hold);
        }

        private AdmiralService BuildAdmiral(TestDatabase testDb, LoggingModule logging, DispatchHold hold)
        {
            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N")),
                DatabasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "unused.db"),
                LogDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "logs"),
                DocksDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "docks"),
                ReposDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "repos")
            };
            settings.InitializeDirectories();

            GitService git = new GitService(logging);
            DockService docks = new DockService(logging, testDb.Driver, settings, git);
            CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains);
            VoyageService voyages = new VoyageService(logging, testDb.Driver);
            return new AdmiralService(logging, testDb.Driver, settings, captains, missions, voyages, docks, null, null, null, null, git, hold);
        }
    }
}
