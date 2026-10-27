namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A battery dispatcher for tests: instead of launching a captain it clones the fixture vessel's repository at its
    /// default branch into a temporary worktree (what a dock would hold after the reset), lets a scripted "captain" edit
    /// it, and returns a finished mission with the scripted output and a local workspace for grading.
    /// </summary>
    public sealed class CannedBatteryTaskDispatcher : IBatteryTaskDispatcher, IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Requests received, in order.
        /// </summary>
        public List<BatteryDispatchRequest> Requests { get; } = new List<BatteryDispatchRequest>();

        /// <summary>
        /// Scripted captain: edits the worktree for a request and returns the agent output. Null returns no output.
        /// </summary>
        public Func<BatteryDispatchRequest, string, string?>? Captain { get; set; } = null;

        /// <summary>
        /// Mission status to report (default WorkProduced).
        /// </summary>
        public MissionStatusEnum Status { get; set; } = MissionStatusEnum.WorkProduced;

        /// <summary>
        /// Per-request mission status, or null to use <see cref="Status"/> for every request.
        /// </summary>
        public Func<BatteryDispatchRequest, MissionStatusEnum>? StatusFor { get; set; } = null;

        /// <summary>
        /// Delete the worktree before returning, as if the dock had been reclaimed.
        /// </summary>
        public bool ReclaimWorktree { get; set; } = false;

        /// <summary>
        /// A canned dispatcher for the C# pack that answers the review tasks and cancels the worktree tasks, so a run of
        /// the real pack completes in seconds without building anything: the Judge controls and the linter pass, the
        /// rubric tasks are pending evaluation, and the five worktree tasks fail as cancelled missions.
        /// </summary>
        /// <returns>The dispatcher.</returns>
        public static CannedBatteryTaskDispatcher ForCSharpPack()
        {
            CannedBatteryTaskDispatcher dispatcher = new CannedBatteryTaskDispatcher();
            dispatcher.StatusFor = r => r.Persona == "Worker" || r.Persona == "Test Engineer" ? MissionStatusEnum.Cancelled : MissionStatusEnum.WorkProduced;
            dispatcher.Captain = (r, worktree) =>
            {
                if (r.Title.Contains("/judge-negative ", StringComparison.Ordinal))
                    return "## Affected Case\nScenario: ValueOf is off-by-one and skips the first SKU.\n\n## Affected Case\nScenario: an unknown SKU throws NullReferenceException, not KeyNotFoundException.\n\n## Affected Case\nScenario: percentOff / 100 is integer division.\n\n[ARMADA:VERDICT] FAIL";
                if (r.Title.Contains("/judge-positive ", StringComparison.Ordinal)) return "## Correctness\nLooks right.\n\n[ARMADA:VERDICT] PASS";
                if (r.Title.Contains("/lint-diff ", StringComparison.Ordinal))
                    return "- `var result`: use an explicit type.\n- Suggest has no XML doc comment.\n- field `threshold` is not _PascalCase.\n- Console.WriteLine in src.";
                if (r.Title.Contains("/architect-plan ", StringComparison.Ordinal)) return "```armada-plan\n{\"missions\":[{\"title\":\"Parser\",\"description\":\"parse\"}]}\n```";
                return "Summary of the voyage.";
            };
            return dispatcher;
        }

        #endregion

        #region Private-Members

        private readonly string _Root = Path.Combine(Path.GetTempPath(), "armada-battery-canned-" + Guid.NewGuid().ToString("N"));
        private readonly LoggingModule _Logging;
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CannedBatteryTaskDispatcher()
        {
            _Logging = new LoggingModule();
            _Logging.Settings.EnableConsole = false;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public Task<BatteryDispatchResult> RunAsync(BatteryDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            int index;
            lock (_Lock)
            {
                Requests.Add(request);
                index = Requests.Count;
            }

            string worktree = Path.Combine(_Root, "wt" + index);
            Directory.CreateDirectory(_Root);
            Git(_Root, "clone", "--quiet", "--branch", request.Vessel.DefaultBranch, request.Vessel.RepoUrl!, worktree);

            string? output = Captain?.Invoke(request, worktree);
            if (ReclaimWorktree) Directory.Delete(worktree, true);
            Mission mission = new Mission(request.Title, request.Description)
            {
                VesselId = request.Vessel.Id,
                Persona = request.Persona,
                CaptainId = request.Captain.Id,
                Status = StatusFor != null ? StatusFor(request) : Status,
                AgentOutput = output
            };
            BatteryDispatchResult result = new BatteryDispatchResult
            {
                VoyageId = "vyg_canned" + index,
                Mission = mission,
                Workspace = new DockBatteryWorkspace(new DockHostResolver(new GitService(_Logging), _Logging, null).Local, worktree)
            };
            return Task.FromResult(result);
        }

        /// <summary>
        /// Run git and fail the test when it fails.
        /// </summary>
        /// <param name="directory">Working directory.</param>
        /// <param name="arguments">Arguments.</param>
        /// <returns>Standard output.</returns>
        public static string Git(string directory, params string[] arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            using (Process process = Process.Start(info)!)
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("git " + String.Join(" ", arguments) + " failed: " + stderr);
                return stdout;
            }
        }

        /// <summary>
        /// Delete the temporary worktrees.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_Root)) Directory.Delete(_Root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        #endregion
    }
}
