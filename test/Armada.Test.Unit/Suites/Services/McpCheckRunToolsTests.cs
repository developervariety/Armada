namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for MCP structured check-run tools.
    /// </summary>
    public class McpCheckRunToolsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Check Run Tools";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("run_check returns structured failure instead of Error object", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_missing",
                    type = CheckRunTypeEnum.Build
                });

                object result = await handlers["run_check"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertContains("check_run_failed", json);
                AssertContains("Vessel not found", json);
                AssertFalse(json.Contains("\"Error\"", StringComparison.OrdinalIgnoreCase), "Expected MCP tool result to avoid top-level Error.");
            }).ConfigureAwait(false);

            await RunTest("retry_check_run returns structured failure instead of Error object", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    checkRunId = "chk_missing"
                });

                object result = await handlers["retry_check_run"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertContains("check_run_retry_failed", json);
                AssertContains("Check run not found", json);
                AssertFalse(json.Contains("\"Error\"", StringComparison.OrdinalIgnoreCase), "Expected MCP retry result to avoid top-level Error.");
            }).ConfigureAwait(false);

            // A retry can wait a long time for the host-wide slot. The call follows run_check's bounded wait,
            // so it returns the unfinished record with its slot wait instead of blocking to the transport timeout.
            await RunTest("retry_check_run returns a retry still waiting for the host slot with its slot wait", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-retry-within-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                IDisposable? hostSlot = null;
                try
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
                    {
                        Name = "retry-within-vessel",
                        RepoUrl = String.Empty,
                        LocalPath = String.Empty,
                        WorkingDirectory = workingDirectory,
                        DefaultBranch = "main"
                    }).ConfigureAwait(false);
                    CheckRun prior = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Lint,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "echo retried",
                        WorkingDirectory = workingDirectory,
                        ExitCode = 1,
                        CompletedUtc = DateTime.UtcNow
                    }).ConfigureAwait(false);

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, TimeSpan.FromMilliseconds(500));
                    hostSlot = await HostWideCommandLock.AcquireAsync("holder_retry_probe", "another vessel gate", CancellationToken.None).ConfigureAwait(false);
                    Task<object> call = handlers["retry_check_run"](JsonSerializer.SerializeToElement(new { checkRunId = prior.Id }));
                    Task first = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                    AssertTrue(ReferenceEquals(first, call), "the retry call returns while the retry still waits for the slot");

                    string json = JsonSerializer.Serialize(await call.ConfigureAwait(false));
                    AssertContains("WaitingForSlot", json, "the unfinished retry reports its slot wait");
                    AssertContains("another vessel gate", json, "the wait names what holds the slot");

                    hostSlot.Dispose();
                    hostSlot = null;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                    CheckRun? retried = null;
                    while (DateTime.UtcNow < deadline)
                    {
                        EnumerationResult<CheckRun> runs = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                        {
                            VesselId = vessel.Id,
                            Status = CheckRunStatusEnum.Passed,
                            PageNumber = 1,
                            PageSize = 10
                        }).ConfigureAwait(false);
                        retried = runs.Objects.Count > 0 ? runs.Objects[0] : null;
                        if (retried != null) break;
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertNotNull(retried, "the retry finishes its own record after the call returned");
                }
                finally
                {
                    hostSlot?.Dispose();
                    try { Directory.Delete(workingDirectory, true); } catch (IOException) { }
                }
            }).ConfigureAwait(false);

            // A retry the unstamped-record rule cancels ends at once. The call returns the Canceled record with the
            // reason in its summary, so the operator sees why the retry did not run instead of a bare cancel.
            await RunTest("retry_check_run returns a retry that ends Canceled with its reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-retry-canceled-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(workingDirectory);
                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
                        {
                            Name = "retry-canceled-vessel",
                            RepoUrl = String.Empty,
                            LocalPath = String.Empty,
                            WorkingDirectory = workingDirectory,
                            DefaultBranch = "main"
                        }).ConfigureAwait(false);
                        Voyage voyage = new Voyage("ended-voyage");
                        voyage.Status = VoyageStatusEnum.Failed;
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        CheckRun prior = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            VesselId = vessel.Id,
                            VoyageId = voyage.Id,
                            Type = CheckRunTypeEnum.Build,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Command = "echo never-run",
                            WorkingDirectory = workingDirectory,
                            Label = "Build (armed at dispatch)"
                        }).ConfigureAwait(false);

                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, TimeSpan.FromMilliseconds(500));
                        Task<object> call = handlers["retry_check_run"](JsonSerializer.SerializeToElement(new { checkRunId = prior.Id }));
                        Task first = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                        AssertTrue(ReferenceEquals(first, call), "the retry call returns instead of waiting to the transport timeout");

                        object result = await call.ConfigureAwait(false);
                        CheckRunSummaryView? view = result as CheckRunSummaryView;
                        AssertNotNull(view, "the retry returns the check record, not a failure: " + JsonSerializer.Serialize(result));
                        AssertEqual(prior.Id, view!.Id, "the retry returns the record it executed");
                        AssertEqual(CheckRunStatusEnum.Canceled, view.Status, "the retry of an unstamped Check of an ended voyage ends Canceled");
                        AssertContains("unstamped_voyage_check", view.Summary ?? String.Empty, "the Canceled retry names why it did not run");

                        CheckRun? stored = await testDb.Driver.CheckRuns.ReadAsync(prior.Id).ConfigureAwait(false);
                        AssertNotNull(stored, "the cancelled record stays readable");
                        AssertContains("unstamped_voyage_check", stored!.Output ?? String.Empty, "the stored output carries the reason");
                        AssertFalse(CheckRunService.IsOperatorRun(prior.Id), "the operator mark ends with the retry");
                    }
                    finally
                    {
                        try { Directory.Delete(workingDirectory, true); } catch (IOException) { }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("armada_resolve_check validation returns structured failure instead of Error object", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    checkRunId = "chk_missing",
                    status = "NotAStatus"
                });

                object result = await handlers["armada_resolve_check"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertContains("check_status_invalid", json);
                AssertContains("ValidStatusValues", json);
                AssertFalse(json.Contains("\"Error\"", StringComparison.OrdinalIgnoreCase), "Expected MCP resolve result to avoid top-level Error.");
            }).ConfigureAwait(false);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterHandlers(DatabaseDriver database, TimeSpan? checkWait = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(database, logging);
            VesselReadinessService readiness = new VesselReadinessService(database, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(database, workflowProfiles, readiness, logging);

            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpCheckRunTools.Register(
                (name, _, _, handler) =>
                {
                    handlers[name] = McpTestCaller.Wrap(handler);
                },
                database,
                checkRuns,
                checkWait);
            return handlers;
        }
    }
}
