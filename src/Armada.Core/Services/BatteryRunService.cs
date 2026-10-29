namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Runs a battery pack against one captain. For each task it resets the fixture repository, dispatches the task
    /// through <see cref="IBatteryTaskDispatcher"/> (a real voyage on the fixture vessel, landing mode None, evaluation
    /// work), and grades the mission's worktree on the Admiral host per the manifest. Each task ends with one grade:
    /// Passed, Failed, or CouldNotRun when the task could not be judged (a missing tool, another captain, a reclaimed
    /// worktree). The run record holds the grades and timings; the attempts behind each grade are the mission's
    /// attempt provenance records.
    /// </summary>
    public class BatteryRunService
    {
        #region Public-Members

        /// <summary>
        /// Minutes a task's mission may run when the manifest sets no timeout.
        /// </summary>
        public const int DefaultTaskTimeoutMinutes = 60;

        /// <summary>
        /// Error recorded on a run that was running when the Admiral stopped.
        /// </summary>
        public const string InterruptedReason = "The Admiral stopped while the run was running.";

        #endregion

        #region Private-Members

        private readonly string _Header = "[BatteryRunService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly BatteryPackCatalog _Catalog;
        private readonly BatteryFixtureService _Fixtures;
        private readonly IBatteryTaskDispatcher _Dispatcher;
        private readonly ConcurrentDictionary<string, Task> _Running = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _RunByCaptain = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="catalog">Battery packs.</param>
        /// <param name="fixtures">Fixture repositories and vessels.</param>
        /// <param name="dispatcher">Runs one task through dispatch.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public BatteryRunService(DatabaseDriver database, LoggingModule logging, BatteryPackCatalog catalog, BatteryFixtureService fixtures, IBatteryTaskDispatcher dispatcher)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _Fixtures = fixtures ?? throw new ArgumentNullException(nameof(fixtures));
            _Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Every pack in the battery directory, valid or not, ordered by identifier.
        /// </summary>
        /// <returns>The packs as the API lists them.</returns>
        public List<BatteryPackInfo> ListPacks()
        {
            return _Catalog.LoadAll().Select(p => p.ToInfo()).ToList();
        }

        /// <summary>
        /// Start a battery run. Validates the request and resolves (or creates, when asked) the fixture vessel before
        /// returning the queued run; the tasks run in the background.
        /// </summary>
        /// <param name="request">Request.</param>
        /// <param name="userId">Caller.</param>
        /// <param name="token">Cancellation token (for starting the run, not for the background work).</param>
        /// <returns>The queued run.</returns>
        /// <exception cref="ArgumentNullException">Thrown when request is null.</exception>
        /// <exception cref="ArgumentException">Thrown for a missing, unknown or malformed pack or captain, a captain that
        /// is not available, a fixture vessel that lands work, or a missing fixture vessel that was not asked to be created.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the captain already has a battery run in progress.</exception>
        public async Task<BatteryRun> StartAsync(BatteryRunRequest request, string? userId, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.CaptainId)) throw new ArgumentException("captainId is required.", nameof(request));
            if (String.IsNullOrWhiteSpace(request.PackId)) throw new ArgumentException("packId is required.", nameof(request));

            Captain captain = await _Database.Captains.ReadAsync(request.CaptainId.Trim(), token).ConfigureAwait(false)
                ?? throw new ArgumentException("Captain '" + request.CaptainId + "' does not exist.", nameof(request));
            BatteryPack pack = _Catalog.Find(request.PackId) ?? throw new ArgumentException("Unknown battery pack '" + request.PackId + "'.", nameof(request));
            if (!pack.IsValid) throw new ArgumentException("Battery pack '" + pack.Id + "' is malformed: " + String.Join(" ", pack.Errors), nameof(request));

            string tenantId = captain.TenantId ?? Constants.DefaultTenantId;
            Vessel vessel = await _Fixtures.EnsureVesselAsync(pack, tenantId, null, request.CreateFixtureVessel, token).ConfigureAwait(false);
            if (vessel.LandingMode != LandingModeEnum.None)
                throw new ArgumentException("Fixture vessel " + vessel.Name + " must have landing mode None so battery work is never landed.", nameof(request));

            BatteryRun run = new BatteryRun
            {
                TenantId = tenantId,
                UserId = userId,
                CaptainId = captain.Id,
                CaptainName = captain.Name,
                PackId = pack.Id,
                PackVersion = pack.Manifest!.Version
            };
            if (!_RunByCaptain.TryAdd(captain.Id, run.Id))
                throw new InvalidOperationException("Captain " + captain.Id + " already has a battery run in progress (" + _RunByCaptain[captain.Id] + ").");

            try
            {
                run = await _Database.BatteryRuns.CreateAsync(run, token).ConfigureAwait(false);
            }
            catch
            {
                _RunByCaptain.TryRemove(captain.Id, out _);
                throw;
            }

            _Running[run.Id] = Task.Run(() => ExecuteAsync(run, pack, vessel, captain));
            return run;
        }

        /// <summary>
        /// Wait for a run started by this instance to finish.
        /// </summary>
        /// <param name="runId">Run identifier.</param>
        /// <returns>The task that completes when the run ends; completed when the run is not running here.</returns>
        public Task WaitAsync(string runId)
        {
            return _Running.TryGetValue(runId, out Task? running) ? running : Task.CompletedTask;
        }

        /// <summary>
        /// Mark every run left Running by a stopped Admiral as Interrupted. Call once at startup, before any run starts.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of runs changed.</returns>
        public Task<int> RecoverInterruptedAsync(CancellationToken token = default)
        {
            return _Database.BatteryRuns.InterruptRunningAsync(InterruptedReason, DateTime.UtcNow, token);
        }

        #endregion

        #region Private-Methods

        private async Task ExecuteAsync(BatteryRun run, BatteryPack pack, Vessel vessel, Captain captain)
        {
            try
            {
                HashSet<string> missingTools = new HashSet<string>(StringComparer.Ordinal);
                foreach (BatteryTaskDefinition task in pack.Manifest!.Tasks)
                {
                    BatteryTaskResult result = await RunTaskAsync(run, pack, task, vessel, captain, missingTools).ConfigureAwait(false);
                    run.Results.Add(result);
                    await _Database.BatteryRuns.UpdateAsync(run).ConfigureAwait(false);
                }

                run.Status = BatteryRunStatusEnum.Completed;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "run " + run.Id + " stopped: " + ex.Message);
                run.Status = BatteryRunStatusEnum.Failed;
                run.Error = ex.Message;
            }
            finally
            {
                run.CompletedUtc = DateTime.UtcNow;
                try
                {
                    await _Database.BatteryRuns.UpdateAsync(run).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not store the end of run " + run.Id + ": " + ex.Message);
                }

                _RunByCaptain.TryRemove(captain.Id, out _);
                _Running.TryRemove(run.Id, out _);
            }
        }

        private async Task<BatteryTaskResult> RunTaskAsync(BatteryRun run, BatteryPack pack, BatteryTaskDefinition task, Vessel vessel, Captain captain, HashSet<string> missingTools)
        {
            BatteryTaskResult result = new BatteryTaskResult { TaskId = task.Id!, Title = task.Title, StartedUtc = DateTime.UtcNow };
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                List<string> needed = pack.ToolchainFor(task);
                string? alreadyMissing = needed.FirstOrDefault(missingTools.Contains);
                if (alreadyMissing != null)
                {
                    Apply(result, CouldNotRun("Not dispatched: " + alreadyMissing + " was found missing on the Admiral host by an earlier task."));
                    return result;
                }

                BatteryDispatchResult dispatch;
                string label = run.Id + " " + captain.Id;
                using (IDisposable lease = await _Fixtures.LockAsync(pack.Id).ConfigureAwait(false))
                {
                    try
                    {
                        await _Fixtures.ResetAsync(pack, task, label).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Apply(result, CouldNotRun("The fixture could not be reset: " + ex.Message));
                        return result;
                    }

                    int timeoutMinutes = task.TimeoutMinutes ?? DefaultTaskTimeoutMinutes;
                    Stopwatch missionClock = Stopwatch.StartNew();
                    dispatch = await _Dispatcher.RunAsync(new BatteryDispatchRequest(vessel, captain, task.Title!, task.Prompt!, timeoutMinutes * 60000)).ConfigureAwait(false);
                    result.MissionDurationMs = dispatch.VoyageId == null ? null : missionClock.ElapsedMilliseconds;
                }

                result.VoyageId = dispatch.VoyageId;
                result.MissionId = dispatch.Mission?.Id;
                Apply(result, await GradeAsync(pack, task, captain, dispatch, needed, missingTools).ConfigureAwait(false));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _Logging.Warn(_Header + "task " + task.Id + " of run " + run.Id + " could not be graded: " + ex.Message);
                Apply(result, CouldNotRun("Grading stopped on an error: " + ex.Message));
            }
            finally
            {
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = clock.ElapsedMilliseconds;
            }

            return result;
        }

        private async Task<BatteryGrade> GradeAsync(BatteryPack pack, BatteryTaskDefinition task, Captain captain, BatteryDispatchResult dispatch, List<string> needed, HashSet<string> missingTools)
        {
            if (dispatch.FailureReason != null) return CouldNotRun(dispatch.FailureReason);
            Mission? mission = dispatch.Mission;
            if (mission == null) return CouldNotRun("The dispatch produced no mission.");
            if (!mission.IsEvaluation) return CouldNotRun("Mission " + mission.Id + " is not marked as evaluation work, so it is not graded.");

            string? foreign = await FindOtherCaptainAsync(mission, captain).ConfigureAwait(false);
            if (foreign != null) return CouldNotRun("Captain " + foreign + " ran the mission instead of captain " + captain.Id + ".");

            if (dispatch.TimedOut)
            {
                if (String.IsNullOrEmpty(mission.CaptainId))
                    return CouldNotRun("The mission was never assigned to captain " + captain.Id + " (the captain was busy or unavailable).");
                return Failed("The mission did not finish within " + (task.TimeoutMinutes ?? DefaultTaskTimeoutMinutes) + " minutes and was cancelled.");
            }

            if (mission.Status == MissionStatusEnum.Failed || mission.Status == MissionStatusEnum.Cancelled || mission.Status == MissionStatusEnum.LandingFailed)
                return Failed("The mission ended " + mission.Status + (String.IsNullOrWhiteSpace(mission.FailureReason) ? String.Empty : ": " + mission.FailureReason));

            IBatteryWorkspace? workspace = dispatch.Workspace;
            if (workspace == null) return CouldNotRun(dispatch.WorkspaceUnavailableReason ?? "There is no worktree to grade (the mission produced no dock).");
            if (!await workspace.ExistsAsync().ConfigureAwait(false))
                return CouldNotRun("The worktree " + workspace.Root + " no longer exists on " + workspace.HostLabel + ".");

            BatteryToolchainCheck toolchain = await BatteryTaskGrader.CheckToolchainAsync(pack, task, workspace).ConfigureAwait(false);
            if (toolchain.Missing.Count > 0)
            {
                foreach (string name in toolchain.Missing) missingTools.Add(name);
                return CouldNotRun(toolchain.Reason ?? "The toolchain is incomplete.");
            }

            return await BatteryTaskGrader.GradeAsync(pack, task, workspace).ConfigureAwait(false);
        }

        private async Task<string?> FindOtherCaptainAsync(Mission mission, Captain captain)
        {
            if (!String.IsNullOrEmpty(mission.CaptainId) && !String.Equals(mission.CaptainId, captain.Id, StringComparison.Ordinal)) return mission.CaptainId;
            ProductionFactPage<MissionAttemptProvenance> attempts = await _Database.MissionAttempts.EnumerateByMissionAsync(mission.TenantId, mission.Id, 1000).ConfigureAwait(false);
            MissionAttemptProvenance? other = attempts.Items.FirstOrDefault(a => !String.IsNullOrEmpty(a.CaptainId) && !String.Equals(a.CaptainId, captain.Id, StringComparison.Ordinal));
            return other?.CaptainId;
        }

        private static void Apply(BatteryTaskResult result, BatteryGrade grade)
        {
            result.Grade = grade.Grade;
            result.Score = grade.Score;
            result.Summary = grade.Summary;
            result.Reason = grade.Reason;
            result.Details = grade.Details;
        }

        private static BatteryGrade CouldNotRun(string reason)
        {
            return new BatteryGrade { Grade = BatteryTaskGradeEnum.CouldNotRun, Reason = reason, Summary = "Could not run." };
        }

        private static BatteryGrade Failed(string reason)
        {
            return new BatteryGrade { Grade = BatteryTaskGradeEnum.Failed, Reason = reason, Summary = "Failed before grading." };
        }

        #endregion
    }
}
