namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// <see cref="MuxHostResolver"/> sends mux to the caller's Harbor (where mux is installed) instead of the Admiral
    /// host, falls back to the Admiral only in standalone mode, and refuses when Harbors are required but none is
    /// connected; <see cref="MuxCliService"/> turns a mux that cannot start into an actionable message.
    /// </summary>
    public sealed class MuxHostResolverSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.MuxHostResolver";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("prefers_callers_harbor", "Mux endpoint discovery runs on the caller's own Harbor, not another user's or the Admiral", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, CreateLogging()), CreateLogging(), null);
                List<string> asked = new List<string>();
                string json = "{\"contractVersion\":1,\"success\":true,\"endpoints\":[{\"name\":\"homedns-gpt-oss-20b\"}]}";
                await LinkAsync(manager, "hbr_other", "usr_other", asked, json).ConfigureAwait(false);
                await LinkAsync(manager, "hbr_mine", "usr_me", asked, json).ConfigureAwait(false);

                MuxHostResolver resolver = new MuxHostResolver(new ArmadaSettings(), testDb.Driver, () => manager, CreateLogging());
                MuxHost host = await resolver.ResolveAsync("ten_m", "usr_me", null, null).ConfigureAwait(false);

                AssertEqual("hbr_mine", host.HarborId, "the caller's Harbor is chosen");
                MuxEndpointListResult result = await new MuxCliService(CreateLogging(), host.Executor).ListEndpointsAsync(null).ConfigureAwait(false);
                AssertTrue(result.Success, "the Harbor's mux answered");
                AssertEqual("homedns-gpt-oss-20b", result.Endpoints[0].Name);
                AssertEqual(1, asked.Count, "one Harbor was asked");
                AssertEqual("hbr_mine:mux", asked[0], "mux ran on the caller's Harbor");
            }));

            cases.Add(CaseAsync("standalone_runs_locally", "With no Harbor connected and Harbors not required, mux runs on the Admiral host", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, CreateLogging()), CreateLogging(), null);
                MuxHostResolver resolver = new MuxHostResolver(new ArmadaSettings(), testDb.Driver, () => manager, CreateLogging());

                MuxHost host = await resolver.ResolveAsync("ten_m", "usr_me", null, null).ConfigureAwait(false);

                AssertTrue(host.Executor is LocalHostCommandExecutor, "local executor");
                AssertFalse(host.IsRemote, "not remote");
                AssertNull(host.Error, "no error");
            }));

            cases.Add(CaseAsync("required_without_harbor_fails", "When Harbors are required and none is connected, resolution reports why instead of running locally", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, CreateLogging()), CreateLogging(), null);
                ArmadaSettings settings = new ArmadaSettings();
                settings.RequireHarborForLaunch = true;
                MuxHostResolver resolver = new MuxHostResolver(settings, testDb.Driver, () => manager, CreateLogging());

                MuxHost host = await resolver.ResolveAsync("ten_m", "usr_me", null, null).ConfigureAwait(false);

                AssertNull(host.Executor, "no executor");
                AssertTrue(host.Error != null && host.Error.Contains("Harbor", StringComparison.Ordinal), "the reason names Harbor");
            }));

            cases.Add(CaseAsync("missing_mux_is_actionable", "A mux that cannot start, locally or on a Harbor, reports that mux runs on a Harbor", TestTags.Negative, async () =>
            {
                MuxCliService local = new MuxCliService(CreateLogging(), new ThrowingExecutor());
                InvalidOperationException localError = await CaptureAsync(() => local.ListEndpointsAsync(null)).ConfigureAwait(false);
                AssertTrue(localError.Message.Contains("Harbor", StringComparison.Ordinal), "local start failure explains Harbor");

                HostCommandResult remoteStartFailure = new HostCommandResult
                {
                    ExitCode = -1,
                    StandardError = "An error occurred trying to start process 'mux' with working directory '/'. No such file or directory"
                };
                MuxCliService remote = new MuxCliService(CreateLogging(), new FixedExecutor(remoteStartFailure));
                InvalidOperationException remoteError = await CaptureAsync(() => remote.ListEndpointsAsync(null)).ConfigureAwait(false);
                AssertFalse(remoteError.Message.Contains("/app", StringComparison.Ordinal), "no raw process error");
                AssertTrue(remoteError.Message.Contains("PATH", StringComparison.Ordinal), "explains the PATH");
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Mux Host Resolver", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task LinkAsync(HarborConnectionManager manager, string harborId, string userId, List<string> asked, string reply)
        {
            HarborSendDelegate send = (message, token) =>
            {
                if (message is HarborGitRequest git)
                {
                    asked.Add(harborId + ":" + git.Executable);
                    return manager.OnMessageAsync(harborId, new HarborGitResult
                    {
                        CorrelationId = git.CorrelationId,
                        RequestId = git.RequestId,
                        ExitCode = 0,
                        StandardOutput = reply
                    }, token);
                }

                return Task.CompletedTask;
            };

            HarborHandshake handshake = new HarborHandshake { HarborId = harborId, Name = harborId, ProtocolVersion = "1.0" };
            handshake.Capabilities.Add(new HarborCapability { Name = "Mux" });
            await manager.OnHandshakeAsync(handshake, "ten_m", userId, send).ConfigureAwait(false);
        }

        private static async Task<InvalidOperationException> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }

            throw new InvalidOperationException("Expected an InvalidOperationException.");
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Classes

        private sealed class ThrowingExecutor : IHostCommandExecutor
        {
            public Task<HostCommandResult> RunAsync(HostCommandRequest request, CancellationToken token = default)
            {
                throw new System.ComponentModel.Win32Exception(2, "An error occurred trying to start process 'mux' with working directory '/app'. No such file or directory");
            }
        }

        private sealed class FixedExecutor : IHostCommandExecutor
        {
            private readonly HostCommandResult _Result;

            public FixedExecutor(HostCommandResult result)
            {
                _Result = result;
            }

            public Task<HostCommandResult> RunAsync(HostCommandRequest request, CancellationToken token = default)
            {
                return Task.FromResult(_Result);
            }
        }

        #endregion
    }
}
