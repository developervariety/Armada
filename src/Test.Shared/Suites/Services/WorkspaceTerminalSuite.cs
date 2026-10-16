namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// The Workspace's Open in Terminal (<see cref="WorkspaceTerminalService"/>) over the in-process Harbor link with real
    /// git repositories: a terminal opens in the checkout the Harbor maps the vessel to, an older Harbor (no
    /// open-terminal capability) is told to update, only the Harbor's owner or an admin may use it, the Harbor refuses a
    /// root that is not its checkout of the vessel, a vessel without a checkout and a browser away from the Admiral get an
    /// answer instead of a terminal, and the Admiral host opens one only for an admin on its own computer. No real
    /// terminal is started: the launchers are replaced.
    /// </summary>
    public sealed class WorkspaceTerminalSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.WorkspaceTerminal";
        private const string OwnerId = "usr_terminal_owner";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("harbor_opens_in_mapped_checkout", "The Harbor's owner opens a terminal in the checkout the Harbor maps the vessel to", TestTags.Positive, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                List<string> opened = new List<string>();
                using InProcessHarbor harbor = await s.ConnectAsync("hbr_term_ok", folder => { opened.Add(folder); return null; }).ConfigureAwait(false);
                Vessel vessel = await s.CreateVesselAsync("app").ConfigureAwait(false);

                WorkspaceOpenTerminalResult result = await s.Service.OpenAsync(s.Owner, vessel, false).ConfigureAwait(false);

                AssertTrue(result.Opened, "opened: " + result.Message);
                AssertEqual(WorkspaceTerminalLocationEnum.Harbor, result.Location);
                AssertNull(result.Message);
                AssertTrue(PathCanonicalizer.AreEquivalent(git.Checkout, result.Path!), "the checkout path is returned");
                AssertEqual(1, opened.Count, "the Harbor launched one terminal");
                AssertTrue(PathCanonicalizer.AreEquivalent(git.Checkout, opened[0]), "in the mapped checkout");
                AssertEqual("hbr_term_ok", result.HostName);
            }));

            cases.Add(CaseAsync("harbor_launch_failure_is_an_answer", "A Harbor that cannot start a terminal answers why, as opened=false with the path", TestTags.Negative, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                using InProcessHarbor harbor = await s.ConnectAsync("hbr_term_fail", folder => "No terminal could be started (xterm: not found).").ConfigureAwait(false);
                Vessel vessel = await s.CreateVesselAsync("app").ConfigureAwait(false);

                WorkspaceOpenTerminalResult result = await s.Service.OpenAsync(s.Owner, vessel, false).ConfigureAwait(false);

                AssertFalse(result.Opened);
                AssertContains("could not open a terminal", result.Message ?? "");
                AssertContains("xterm: not found", result.Message ?? "");
                AssertTrue(PathCanonicalizer.AreEquivalent(git.Checkout, result.Path!), "the path to open by hand");
            }));

            cases.Add(CaseAsync("older_harbor_is_told_to_update", "A Harbor without the open-terminal capability is not sent the operation and the answer says to update it", TestTags.Negative, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                using InProcessHarbor harbor = await s.ConnectAsync("hbr_term_old", null).ConfigureAwait(false);
                Vessel vessel = await s.CreateVesselAsync("app").ConfigureAwait(false);

                AssertFalse(s.Manager.OpensTerminals("hbr_term_old"), "no capability advertised");
                WorkspaceOpenTerminalResult result = await s.Service.OpenAsync(s.Owner, vessel, false).ConfigureAwait(false);

                AssertFalse(result.Opened);
                AssertEqual(WorkspaceTerminalLocationEnum.Harbor, result.Location);
                AssertEqual("Update the Harbor on hbr_term_old to open a terminal from the dashboard.", result.Message);
                AssertTrue(PathCanonicalizer.AreEquivalent(git.Checkout, result.Path!), "the path to open by hand");
            }));

            cases.Add(CaseAsync("harbor_requires_owner_or_admin", "Only the Harbor's owner or an admin of its tenant may open a terminal on it", TestTags.Negative, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                List<string> opened = new List<string>();
                using InProcessHarbor harbor = await s.ConnectAsync("hbr_term_auth", folder => { opened.Add(folder); return null; }).ConfigureAwait(false);
                Vessel vessel = await s.CreateVesselAsync("app").ConfigureAwait(false);

                AuthContext stranger = AuthContext.Authenticated(Constants.DefaultTenantId, "usr_terminal_stranger", false, false, "UnitTest");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => s.Service.OpenAsync(stranger, vessel, false)).ConfigureAwait(false);
                AssertEqual(0, opened.Count, "nothing launched for a stranger");

                AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, "usr_terminal_tadmin", false, true, "UnitTest");
                WorkspaceOpenTerminalResult result = await s.Service.OpenAsync(tenantAdmin, vessel, false).ConfigureAwait(false);
                AssertTrue(result.Opened, "a tenant admin may: " + result.Message);
                AssertEqual(1, opened.Count);
            }));

            cases.Add(CaseAsync("harbor_refuses_roots_that_are_not_the_checkout", "The Harbor opens a terminal only in its mapped checkout of the vessel, never in a dock or another folder the Admiral names", TestTags.Negative, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                List<string> opened = new List<string>();
                using InProcessHarbor harbor = await s.ConnectAsync("hbr_term_root", folder => { opened.Add(folder); return null; }).ConfigureAwait(false);

                string inDocks = Path.Combine(git.HarborDocks, "app", "msn_x");
                Directory.CreateDirectory(inDocks);
                HarborFileResult dock = await s.SendOpenTerminalAsync("hbr_term_root", inDocks, "app").ConfigureAwait(false);
                AssertFalse(dock.Success, "a dock is not the checkout");
                AssertEqual(HarborFileErrorCodeEnum.Refused, dock.ErrorCode);

                string elsewhere = TestTemp.NewDirectory("terminal-elsewhere");
                HarborFileResult other = await s.SendOpenTerminalAsync("hbr_term_root", elsewhere, "app").ConfigureAwait(false);
                AssertFalse(other.Success, "an arbitrary folder is refused");
                AssertEqual(HarborFileErrorCodeEnum.Refused, other.ErrorCode);

                HarborFileResult wrongVessel = await s.SendOpenTerminalAsync("hbr_term_root", git.Checkout, "other-vessel").ConfigureAwait(false);
                AssertFalse(wrongVessel.Success, "the checkout of a different vessel is refused");

                AssertEqual(0, opened.Count, "nothing launched");
                HarborFileResult mapped = await s.SendOpenTerminalAsync("hbr_term_root", git.Checkout, "app").ConfigureAwait(false);
                AssertTrue(mapped.Success, "the mapped checkout opens: " + mapped.Message);
                AssertEqual(1, opened.Count);
            }));

            cases.Add(CaseAsync("no_checkout_is_an_answer", "A vessel without a checkout anywhere gets opened=false with the reason", TestTags.Negative, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                Vessel vessel = await s.CreateVesselAsync("app").ConfigureAwait(false);

                WorkspaceOpenTerminalResult result = await s.Service.OpenAsync(s.Owner, vessel, true).ConfigureAwait(false);

                AssertFalse(result.Opened);
                AssertEqual(WorkspaceTerminalLocationEnum.None, result.Location);
                AssertFalse(String.IsNullOrWhiteSpace(result.Message), "says why");
            }));

            cases.Add(CaseAsync("admiral_host_needs_this_computer_and_an_admin", "A working directory on the Admiral host opens a terminal only for an admin whose browser is on the Admiral's computer", TestTags.Negative, async () =>
            {
                using HarborDockGitFixture git = await HarborDockGitFixture.CreateAsync().ConfigureAwait(false);
                using Scenario s = await Scenario.CreateAsync(git).ConfigureAwait(false);
                Vessel vessel = await s.CreateVesselAsync("app", git.Checkout).ConfigureAwait(false);
                List<string> opened = new List<string>();
                s.Service.LocalLauncher = folder => { opened.Add(folder); return null; };

                WorkspaceOpenTerminalResult remote = await s.Service.OpenAsync(s.Admin, vessel, false).ConfigureAwait(false);
                AssertFalse(remote.Opened, "a browser elsewhere would not see it");
                AssertEqual(WorkspaceTerminalLocationEnum.AdmiralHost, remote.Location);
                AssertContains("browser is elsewhere", remote.Message ?? "");
                AssertTrue(PathCanonicalizer.AreEquivalent(git.Checkout, remote.Path!), "the path to open by hand");
                AssertEqual(0, opened.Count);

                await AssertThrowsAsync<UnauthorizedAccessException>(() => s.Service.OpenAsync(s.Owner, vessel, true)).ConfigureAwait(false);
                AssertEqual(0, opened.Count, "not for a non-admin");

                WorkspaceOpenTerminalResult local = await s.Service.OpenAsync(s.Admin, vessel, true).ConfigureAwait(false);
                AssertTrue(local.Opened, "opened on this computer");
                AssertEqual(Environment.MachineName, local.HostName);
                AssertEqual(1, opened.Count);

                s.Service.LocalLauncher = folder => "The Admiral runs in a container, which has no desktop to open a terminal on.";
                WorkspaceOpenTerminalResult container = await s.Service.OpenAsync(s.Admin, vessel, true).ConfigureAwait(false);
                AssertFalse(container.Opened);
                AssertContains("container", container.Message ?? "");
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Workspace: Open in Terminal", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (T)
            {
                return;
            }

            throw new AssertionException("Expected " + typeof(T).Name);
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

        #region Private-Types

        private sealed class Scenario : IDisposable
        {
            public TestDatabase Db { get; }

            public LoggingModule Logging { get; }

            public HarborConnectionManager Manager { get; }

            public WorkspaceTerminalService Service { get; }

            public AuthContext Admin { get; }

            public AuthContext Owner { get; }

            private readonly HarborDockGitFixture _Git;

            private Scenario(TestDatabase db, HarborDockGitFixture git)
            {
                Db = db;
                _Git = git;
                Logging = new LoggingModule();
                Logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                settings.LogDirectory = Path.Combine(git.AdmiralData, "logs");
                settings.DocksDirectory = Path.Combine(git.AdmiralData, "docks");
                settings.ReposDirectory = Path.Combine(git.AdmiralData, "repos");
                Manager = new HarborConnectionManager(new HarborService(db.Driver, Logging), Logging, null);
                Service = new WorkspaceTerminalService(new VesselHostResolver(db.Driver, settings, Logging, Manager));
                Admin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");
                Owner = AuthContext.Authenticated(Constants.DefaultTenantId, OwnerId, false, false, "UnitTest");
            }

            public static async Task<Scenario> CreateAsync(HarborDockGitFixture git)
            {
                TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                return new Scenario(db, git);
            }

            public Task<InProcessHarbor> ConnectAsync(string harborId, Func<string, string?>? launcher)
            {
                HarborDockSettings settings = _Git.Settings.Clone();
                settings.Repositories.Add(new HarborRepositoryMapping("app", _Git.Checkout));
                HarborDockManager docks = new HarborDockManager(() => settings, Logging);
                docks.TerminalLauncher = launcher;
                return InProcessHarbor.ConnectAsync(Manager, harborId, Constants.DefaultTenantId, OwnerId, new AgentRuntimeFactory(Logging),
                    new List<string> { "git" }, Logging, null, null, docks);
            }

            public async Task<Vessel> CreateVesselAsync(string name, string? workingDirectory = null)
            {
                Vessel vessel = new Vessel(name, _Git.Origin);
                vessel.TenantId = Constants.DefaultTenantId;
                vessel.UserId = Constants.DefaultUserId;
                vessel.DefaultBranch = "main";
                vessel.WorkingDirectory = workingDirectory;
                return await Db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            }

            public async Task<HarborFileResult> SendOpenTerminalAsync(string harborId, string root, string vesselName)
            {
                HarborFileRequest request = new HarborFileRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Operation = HarborFileOperationEnum.OpenTerminal,
                    Root = root,
                    Path = String.Empty,
                    VesselId = "vsl_not_registered",
                    VesselName = vesselName
                };
                HarborFileResult? result = await Manager.SendFileAsync(harborId, request, 30000).ConfigureAwait(false);
                if (result == null) throw new AssertionException("Harbor " + harborId + " did not answer the file request");
                return result;
            }

            public void Dispose()
            {
                Db.Dispose();
            }
        }

        #endregion
    }
}
