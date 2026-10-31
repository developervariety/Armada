namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Evaluates whether a Worker mission's in-dock build and unit-test commands pass before
    /// the mission is accepted as complete. Missions that carry the configured doc-only opt-out
    /// marker are skipped without running any commands. Non-Worker personas are also skipped
    /// unless the settings explicitly list them under AppliedPersonas.
    /// </summary>
    public partial class DefinitionOfDoneGate
    {
        #region Public-Members

        /// <summary>
        /// Named skip reason recorded when a read-only (Audit or Research) mission reaches the gate without
        /// producing a commit: its dock head still equals the dock start commit, so the build and unit-test
        /// commands would measure only the base branch, and a base branch that is already red would fail a
        /// mission that changed nothing.
        /// </summary>
        public const string ReadOnlyNoCommitSkipReason = "read_only_no_commit";

        /// <summary>
        /// Whether the gate is on. Read from the live settings section on every call, so a settings
        /// reload turns the gate on or off without a restart.
        /// </summary>
        public bool IsEnabled
        {
            get => _Settings.Enabled;
        }

        #endregion

        #region Private-Members

        private readonly string _Header = "[DefinitionOfDoneGate] ";

        // The host slot the running evaluation holds. A gate takes the slot at its first command and keeps it
        // across its build, tests and consumer commands until the evaluation ends. See HostWideCommandLock.
        private static readonly AsyncLocal<GateSlot?> _GateSlot = new AsyncLocal<GateSlot?>();
        private readonly DefinitionOfDoneSettings _Settings;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly IContainerRuntimeProbe? _ContainerRuntimeProbe;
        private readonly IGitService? _Git;
        private readonly TypedFlakeScoreAdapter? _FlakeScoreAdapter;
        private readonly HostLoadMonitor _HostLoad;
        private readonly DefinitionOfDoneFailureClassifier _FailureClassifier = new DefinitionOfDoneFailureClassifier();

        private const int _CommandOutputLimitBytes = 16 * 1024 * 1024;
        private const int _MAX_DIAGNOSTIC_TEXT_CHARS = 16000;
        private const int _MAX_SECTION_CHARS = 7800;
        private const int _MAX_LINE_CHARS = 2000;
        private const string _FullGateOutputPointer = "armada_get_mission_log with the captureId from the definition-of-done report";

        // Lock reason on every consumer-verification worktree, so an operator listing the repository's
        // worktrees can tell whose lock it is.
        private const string ConsumerVerifyLockReason = "armada consumer verification in progress";

        // Quiet load readings in a row a consumer build needs after a busy reading (see HostLoadMonitor.WaitForQuietHostAsync).
        private const int ConsumerBuildQuietReadings = 3;

        private static readonly Regex _SecretLikePattern = new Regex(
            @"(?:password|passwd|secret|token|key|credential|auth|api_key|apikey|access_key|private_key)\s*[=:]\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with required dependencies.
        /// </summary>
        /// <param name="settings">Gate configuration.</param>
        /// <param name="database">Database driver for resolving vessel and workflow profile data.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="containerRuntimeProbe">
        /// Optional container-runtime probe. When supplied and a vessel declares a containerless
        /// unit-test command, the gate falls back to that command if no runtime is available.
        /// Null disables the pre-flight and preserves the original behavior.
        /// </param>
        /// <param name="gitService">
        /// Optional git seam used to provision declared consumers for verification. Null disables
        /// consumer verification entirely, preserving the original behavior for every caller that
        /// does not supply it.
        /// </param>
        /// <param name="flakeScoreAdapter">
        /// Optional D15 <c>flake_score</c> adapter. When supplied and a unit-test command fails as a
        /// test failure, the gate scores the failure; if the model recommends it (Gate mode at or above
        /// threshold), the gate runs an isolated class-filtered re-run of only the failing classes and
        /// the re-run's real result becomes the truth. Null — the default, and the effective state
        /// whenever the decision is Off — preserves the original behavior exactly.
        /// </param>
        /// <param name="hostLoad">
        /// Optional host load monitor. A gate due to start while it reports the host overloaded waits first, and a
        /// gate whose command hits its time limit after a run under load waits for load to fall and runs once more.
        /// The server passes the production monitor, which reads <c>/proc/loadavg</c>. Null reads every load as
        /// unknown, so the gate never waits on load.
        /// </param>
        public DefinitionOfDoneGate(
            DefinitionOfDoneSettings settings,
            DatabaseDriver database,
            LoggingModule logging,
            IContainerRuntimeProbe? containerRuntimeProbe = null,
            IGitService? gitService = null,
            TypedFlakeScoreAdapter? flakeScoreAdapter = null,
            HostLoadMonitor? hostLoad = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _ContainerRuntimeProbe = containerRuntimeProbe;
            _Git = gitService;
            _FlakeScoreAdapter = flakeScoreAdapter;
            _HostLoad = hostLoad ?? new HostLoadMonitor(() => null);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evaluate the definition-of-done gate for the specified mission and dock.
        /// Returns a skipped result when the gate does not apply; returns a passing result
        /// when all required commands succeed; returns a failing result with the command
        /// label, exit code, and output tail when any command fails.
        /// </summary>
        /// <param name="mission">The mission being completed.</param>
        /// <param name="dock">The captain's dock, used to locate the worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="stageStartCommit">Commit the dock started from, when known. A stage the gate does not
        /// apply to uses it to read only its own change when deciding whether to re-verify consumers.</param>
        /// <returns>A <see cref="DefinitionOfDoneResult"/> describing the gate outcome.</returns>
        /// <remarks>
        /// A result classified <c>Timeout</c> while the host is overloaded (see <see cref="HostLoadMonitor"/>)
        /// does not stand on its own: the gate releases the host slot, waits until load falls or
        /// <see cref="HostLoadMonitor.MaximumWait"/> ends, records a <see cref="DefinitionOfDoneLoadRerunRecord"/>
        /// event, and evaluates once more. A result whose command hit its time limit while it shared the host command
        /// slots with another build or test command, or while it was still writing output (see
        /// <see cref="LiveOutputWindow"/>), is evaluated once more the same way, without the load wait. The re-run
        /// holds the host alone (<see cref="HostWideCommandLock.AcquireAloneAsync"/>): it waits until no other build
        /// or test command holds a slot, and no other is granted while it runs, so it cannot meet the same contention
        /// again. Its time limits start when it holds the host. The second result stands, whatever it is; a second
        /// time limit that still reads as host load is marked <see cref="DefinitionOfDoneResult.HostLoadTimeout"/>. A
        /// <c>Timeout</c> at normal or unknown load on a silent command that held the host alone stands at once.
        /// </remarks>
        /// <exception cref="DefinitionOfDoneGateCancelledException">The mission or its voyage was cancelled while the gate ran,
        /// waited for the host-wide command slot, or waited for host load to fall. The command process is stopped
        /// and the slot released first.</exception>
        public async Task<DefinitionOfDoneResult> EvaluateAsync(
            Mission mission,
            Dock dock,
            CancellationToken token = default,
            string? stageStartCommit = null)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (dock == null) throw new ArgumentNullException(nameof(dock));

            // The run is registered under the mission so a mission cancel can stop it: the gate observes the
            // run's token, which the cancel trips, and every wait and command below honors that token.
            using IDisposable executionBudgetScope = EnterExecutionBudgetLedger();
            using (DefinitionOfDoneGateRun run = DefinitionOfDoneGateRuns.Start(mission.Id, token))
            {
                DefinitionOfDoneResult result;
                List<DefinitionOfDoneCommandPass> fullPasses = new List<DefinitionOfDoneCommandPass>();
                try
                {
                    // Register before reading durable cancellation state: cancellation either sees
                    // this run, or this read sees the cancellation, even when admission starts late.
                    Mission? storedMission = await _Database.Missions.ReadAsync(mission.Id, run.Token).ConfigureAwait(false);
                    string? voyageId = storedMission?.VoyageId ?? mission.VoyageId;
                    bool cancelled = (storedMission ?? mission).Status == MissionStatusEnum.Cancelled;
                    if (!cancelled && !String.IsNullOrEmpty(voyageId))
                    {
                        Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, run.Token).ConfigureAwait(false);
                        cancelled = voyage?.Status == VoyageStatusEnum.Cancelled;
                    }
                    if (cancelled)
                    {
                        run.CancelForMission();
                        run.Token.ThrowIfCancellationRequested();
                    }

                    // A gate that starts on an overloaded host spends its command time limits on the host's
                    // queue, not on the change, so it waits for the host first. A gate that will not run
                    // commands does not wait.
                    if (ResolveSkipReason(mission) == null)
                        await WaitForHostAdmissionAsync(mission, dock, run.Token).ConfigureAwait(false);

                    result = await EvaluateAttemptAsync(mission, dock, run, stageStartCommit, fullPasses).ConfigureAwait(false);

                    // A whole host running several times slower than normal times out commands that pass at
                    // normal load. Such a time limit says little about the change, so the gate waits for load
                    // to fall and runs once more; the second result stands. The run counts as overloaded when
                    // the 15-minute average says so, because the 1-minute average drops as the timed-out
                    // command's own processes exit. A command that shared the host slots with another build or test
                    // command can spend its limit on that command's load, or waiting on a lock both take inside their
                    // commands (a vessel suite lock, for example), at any load average; it runs once more as well, and so
                    // does a command still writing output at its limit, which was working rather than hung. The re-run
                    // holds the host alone, so neither another command's load nor a lock both take can reach it.
                    if (!run.CancelledByMission && !result.Passed && result.TimedOut && DeclaredExecutionBudgetFailed)
                    {
                        // Keep the actual host context without replenishing an exhausted budget.
                        HostLoadReading atLimit = _HostLoad.Read();
                        result.HostLoadTimeout |= result.FailureClass == DefinitionOfDoneFailureClassEnum.Timeout
                            && (_HostLoad.RanOverloaded(atLimit) || result.TimedOutOnSharedHost || result.TimedOutWhileProgressing);
                    }
                    if (!run.CancelledByMission
                        && !result.Passed
                        && result.TimedOut
                        && !DeclaredExecutionBudgetFailed)
                    {
                        HostLoadReading atTimeout = _HostLoad.Read();
                        bool overloaded = _HostLoad.RanOverloaded(atTimeout);
                        if (overloaded || result.TimedOutOnSharedHost || result.TimedOutWhileProgressing)
                        {
                            HostLoadReading atRerun = overloaded
                                ? await WaitForHostLoadToFallAsync(mission, dock, atTimeout, run.Token).ConfigureAwait(false)
                                : atTimeout;
                            await RecordHostLoadRerunAsync(mission, result, overloaded, atTimeout, atRerun, token).ConfigureAwait(false);
                            result = await EvaluateAttemptAsync(mission, dock, run, stageStartCommit, fullPasses, alone: true).ConfigureAwait(false);

                            // A second time limit that still reads as host load is the host's, not the change's: the
                            // reason says so, and recovery may carry the work through one fresh gate.
                            if (!run.CancelledByMission
                                && !result.Passed
                                && result.TimedOut
                                && result.FailureClass == DefinitionOfDoneFailureClassEnum.Timeout)
                            {
                                HostLoadReading atSecondTimeout = _HostLoad.Read();
                                result.HostLoadTimeout = _HostLoad.RanOverloaded(atSecondTimeout)
                                    || result.TimedOutOnSharedHost
                                    || result.TimedOutWhileProgressing;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (run.CancelledByMission && !token.IsCancellationRequested)
                {
                    _Logging.Info(_Header + "gate for mission " + mission.Id + " stopped: the mission or its voyage was cancelled");
                    throw new DefinitionOfDoneGateCancelledException(mission.Id);
                }

                // A cancel can surface as an ordinary result, such as an interrupted command read as a failure.
                // That result describes a run the cancel cut short, so it is never reported as the gate outcome.
                if (run.CancelledByMission && !token.IsCancellationRequested)
                {
                    _Logging.Info(_Header + "gate for mission " + mission.Id + " stopped: the mission or its voyage was cancelled");
                    throw new DefinitionOfDoneGateCancelledException(mission.Id);
                }

                _ExecutionBudgets.Value?.AppendSummary(result);
                return result;
            }
        }

        /// <summary>
        /// Wait while the host is overloaded before the gate's first command, for at most
        /// <see cref="HostLoadMonitor.MaximumWait"/>, and record the wait as a scoped event with a named reason. The
        /// host slot is not held, so other work runs meanwhile; the dock lease is held so the worktree survives the
        /// wait. A host that is not overloaded starts the gate at once and records nothing.
        /// </summary>
        private async Task WaitForHostAdmissionAsync(Mission mission, Dock dock, CancellationToken token)
        {
            HostLoadReading atAdmission = _HostLoad.Read();
            if (!_HostLoad.IsOverloaded(atAdmission)) return;

            _Logging.Warn(_Header + "gate for mission " + mission.Id + " waiting to start: "
                + DefinitionOfDoneLoadAdmissionRecord.HostOverloadedReason + " (" + DescribeLoad(atAdmission) + "); waiting up to "
                + HostLoadMonitor.MaximumWait.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + " minutes for load to fall to "
                + HostLoadMonitor.OverloadThreshold(atAdmission.LogicalCores).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " or below");

            HostLoadReading atStart;
            DockLeaseRegistry.Acquire(dock.Id);
            try
            {
                atStart = await _HostLoad.WaitWhileOverloadedAsync(token).ConfigureAwait(false);
            }
            finally
            {
                DockLeaseRegistry.Release(dock.Id);
            }

            bool loadFell = !_HostLoad.IsOverloaded(atStart);
            TimeSpan waited = atStart.ReadUtc - atAdmission.ReadUtc;
            if (waited < TimeSpan.Zero) waited = TimeSpan.Zero;
            DefinitionOfDoneLoadAdmissionRecord record = new DefinitionOfDoneLoadAdmissionRecord
            {
                LoadAtAdmission = atAdmission.LoadAverage1Min,
                LogicalCores = atAdmission.LogicalCores,
                OverloadThreshold = HostLoadMonitor.OverloadThreshold(atAdmission.LogicalCores),
                LoadAtStart = atStart.LoadAverage1Min,
                LoadFell = loadFell,
                WaitedSeconds = waited.TotalSeconds,
                MaximumWaitSeconds = HostLoadMonitor.MaximumWait.TotalSeconds,
                StartedUtc = atStart.ReadUtc
            };

            string message = "Definition-of-done gate waited " + waited.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + " s to start: " + DefinitionOfDoneLoadAdmissionRecord.HostOverloadedReason + " (" + DescribeLoad(atAdmission) + "); "
                + (loadFell ? "starting after load fell (" : "starting after the maximum wait (") + DescribeLoad(atStart) + ")";
            _Logging.Info(_Header + "mission " + mission.Id + ": " + message);
            await RecordGateEventAsync(mission, DefinitionOfDoneLoadAdmissionRecord.EventType, message, JsonSerializer.Serialize(record), "load admission", token).ConfigureAwait(false);
        }

        /// <summary>
        /// Write one scoped gate event. A failed write is logged and never changes the gate outcome.
        /// </summary>
        private async Task RecordGateEventAsync(Mission mission, string eventType, string message, string payload, string what, CancellationToken token)
        {
            try
            {
                ArmadaEvent gateEvent = new ArmadaEvent(eventType, message);
                EventOwnerScope.ApplyFromMission(gateEvent, mission);
                gateEvent.EntityType = "mission";
                gateEvent.EntityId = mission.Id;
                gateEvent.CaptainId = mission.CaptainId;
                gateEvent.MissionId = mission.Id;
                gateEvent.VesselId = mission.VesselId;
                gateEvent.VoyageId = mission.VoyageId;
                gateEvent.Payload = payload;
                await _Database.Events.CreateAsync(gateEvent, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record the " + what + " for mission " + mission.Id
                    + " exceptionType=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Run one evaluation under its own host slot, released when the evaluation ends. An evaluation that runs
        /// <paramref name="alone"/> holds the whole host instead of one slot.
        /// </summary>
        private async Task<DefinitionOfDoneResult> EvaluateAttemptAsync(
            Mission mission,
            Dock dock,
            DefinitionOfDoneGateRun run,
            string? stageStartCommit,
            List<DefinitionOfDoneCommandPass> fullPasses,
            bool alone = false)
        {
            CancellationToken token = run.Token;
            fullPasses.Clear();
            GateSlot slot = new GateSlot(mission.Id, mission.VesselId, fullPasses, alone);
            _GateSlot.Value = slot;
            try
            {
                DefinitionOfDoneResult result = await EvaluateCoreAsync(mission, dock, token, stageStartCommit).ConfigureAwait(false);
                if (result.Passed && String.IsNullOrEmpty(result.SkippedReason))
                {
                    // Publish only complete passing attempts, before a waiting check can acquire the
                    // slot. A cancelled or failed gate cannot vouch for any command it ran.
                    if (run.CancelledByMission) throw new OperationCanceledException(token);
                    token.ThrowIfCancellationRequested();
                    foreach (DefinitionOfDoneCommandPass pass in fullPasses) DefinitionOfDoneCommandPasses.Record(pass);
                }
                return result;
            }
            finally
            {
                slot.Release();
                _GateSlot.Value = null;
            }
        }

        /// <summary>
        /// Wait for host load to fall before the re-run of a gate that timed out under load. The host slot is not
        /// held, so other work runs meanwhile; the dock lease is held so the worktree survives the wait.
        /// </summary>
        private async Task<HostLoadReading> WaitForHostLoadToFallAsync(
            Mission mission,
            Dock dock,
            HostLoadReading atTimeout,
            CancellationToken token)
        {
            _Logging.Warn(_Header + "gate for mission " + mission.Id + " hit a command time limit while the host ran overloaded ("
                + DescribeLoad(atTimeout) + "); waiting up to "
                + HostLoadMonitor.MaximumWait.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                + " minutes for load to fall to "
                + HostLoadMonitor.RecoveredThreshold(atTimeout.LogicalCores).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " before running the gate once more");

            DockLeaseRegistry.Acquire(dock.Id);
            try
            {
                return await _HostLoad.WaitForLoadToFallAsync(token).ConfigureAwait(false);
            }
            finally
            {
                DockLeaseRegistry.Release(dock.Id);
            }
        }

        /// <summary>
        /// Record the re-run after a time limit and the reading that triggered it as a scoped event. A failed write is
        /// logged and never changes the gate outcome.
        /// </summary>
        private async Task RecordHostLoadRerunAsync(
            Mission mission,
            DefinitionOfDoneResult timedOut,
            bool overloaded,
            HostLoadReading atTimeout,
            HostLoadReading atRerun,
            CancellationToken token)
        {
            bool loadFell = _HostLoad.HasFallen(atRerun);
            TimeSpan waited = atRerun.ReadUtc - atTimeout.ReadUtc;
            if (waited < TimeSpan.Zero) waited = TimeSpan.Zero;
            DefinitionOfDoneLoadRerunRecord record = new DefinitionOfDoneLoadRerunRecord
            {
                Reason = overloaded
                    ? DefinitionOfDoneLoadRerunRecord.HostOverloadedReason
                    : timedOut.TimedOutOnSharedHost
                        ? DefinitionOfDoneLoadRerunRecord.HostSlotSharedReason
                        : DefinitionOfDoneLoadRerunRecord.OutputProgressingReason,
                SharedHostSlot = timedOut.TimedOutOnSharedHost,
                OutputProgressing = timedOut.TimedOutWhileProgressing,
                RerunAlone = true,
                TimedOutCommandLabel = timedOut.CommandLabel,
                LoadAtTimeout = atTimeout.LoadAverage1Min,
                LongLoadAtTimeout = atTimeout.LoadAverage15Min,
                LogicalCores = atTimeout.LogicalCores,
                OverloadThreshold = HostLoadMonitor.OverloadThreshold(atTimeout.LogicalCores),
                RecoveredThreshold = HostLoadMonitor.RecoveredThreshold(atTimeout.LogicalCores),
                LoadAtRerun = atRerun.LoadAverage1Min,
                LoadFell = loadFell,
                WaitedSeconds = waited.TotalSeconds,
                MaximumWaitSeconds = HostLoadMonitor.MaximumWait.TotalSeconds,
                RerunStartedUtc = atRerun.ReadUtc
            };

            string command = "Definition-of-done gate " + (timedOut.CommandLabel ?? "command") + " command timed out ";
            string message = overloaded
                ? "Definition-of-done gate timed out under host load (" + DescribeLoad(atTimeout) + "); "
                    + (loadFell ? "re-running after load fell (" : "re-running after the maximum wait (") + DescribeLoad(atRerun) + ")"
                : timedOut.TimedOutOnSharedHost
                    ? command + "while another build or test command held a host command slot at the same time (" + DescribeLoad(atTimeout) + ")"
                    : command + "while it was still writing output (" + DescribeLoad(atTimeout) + ")";
            message += "; re-running once with the host to itself";
            _Logging.Info(_Header + "mission " + mission.Id + ": " + message);
            await RecordGateEventAsync(mission, DefinitionOfDoneLoadRerunRecord.EventType, message, JsonSerializer.Serialize(record), "load re-run", token).ConfigureAwait(false);
        }

        private static string DescribeLoad(HostLoadReading reading)
        {
            string load = reading.LoadAverage1Min.HasValue
                ? reading.LoadAverage1Min.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
            string longLoad = reading.LoadAverage15Min.HasValue
                ? ", 15-minute load " + reading.LoadAverage15Min.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : String.Empty;
            return "1-minute load " + load + longLoad + " on "
                + reading.LogicalCores.ToString(System.Globalization.CultureInfo.InvariantCulture) + " logical cores";
        }

        private async Task<DefinitionOfDoneResult> EvaluateCoreAsync(
            Mission mission,
            Dock dock,
            CancellationToken token,
            string? stageStartCommit)
        {
            string? skipReason = ResolveSkipReason(mission);
            if (skipReason != null)
            {
                if (!AppliesLaterStageConsumerCheck(mission))
                    return DefinitionOfDoneResult.Skipped(skipReason);
                return await EvaluateLaterStageConsumersAsync(mission, dock, stageStartCommit, skipReason, token).ConfigureAwait(false);
            }

            string? worktreePath = dock.WorktreePath;
            if (String.IsNullOrWhiteSpace(worktreePath))
            {
                string diagnostic = BuildDiagnosticText("Dock has no WorktreePath; cannot run in-dock checks.");
                return DefinitionOfDoneResult.Fail(
                    "dock-setup",
                    -1,
                    diagnostic,
                    DefinitionOfDoneFailureClassEnum.Infra);
            }
            bool harborOwned = IsHarborOwnedMission(mission, dock);
            if (harborOwned && (!Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(worktreePath) || dock.HostCheckout == null))
                return DefinitionOfDoneResult.Fail("dock-setup", -1,
                    BuildDiagnosticText("harbor_owned_gate_binding_missing"), DefinitionOfDoneFailureClassEnum.Infra);

            WorkflowProfile? profile = await ResolveProfileAsync(mission, token).ConfigureAwait(false);
            string? buildCommand = profile?.BuildCommand;
            string? testCommand = profile?.UnitTestCommand;

            if (String.IsNullOrWhiteSpace(buildCommand) && String.IsNullOrWhiteSpace(testCommand))
            {
                return DefinitionOfDoneResult.Fail(
                    "missing-commands",
                    -1,
                    BuildDiagnosticText("No BuildCommand or UnitTestCommand is configured on the vessel's workflow profile. " +
                    "Add a workflow profile for this vessel, or add '" + _Settings.DocOnlyMarker +
                    "' to the mission description to opt out of in-dock verification."),
                    DefinitionOfDoneFailureClassEnum.Infra);
            }

            // Serialize the expensive half host-wide. A gate runs the vessel's full build and unit-test
            // command, and more of those at once than the host can carry produce a failure that looks like broken
            // code but is not: a burst of many simultaneous sub-millisecond failures across unrelated
            // test classes, classified Timeout, where the same command passes alone. The mission that
            // loses the race is marked Failed and its whole downstream pipeline is cancelled.
            //
            // Mission status cannot be the interlock. WorkProduced is persisted when the agent
            // finishes, before this gate runs, and the vessel-mutex query counts only
            // Assigned/InProgress -- so a vessel looks idle for the entire duration of its own gate
            // and the next mission is admitted underneath it. Widening that query to include
            // WorkProduced would deadlock every vessel holding a stranded WorkProduced mission.
            // The contended resource is the host, not the vessel, so the lock belongs here.
            //
            // The gate takes the host-wide slot at its first command and keeps it until the evaluation
            // ends (RunCommandAsync), so its tests and consumer suites follow its build without
            // re-queueing; a check that asks while the gate runs waits for the rest of it. Check runs
            // and merge-queue test runs share the slot, so a gate command never overlaps a check or a
            // merge-queue test.
            //
            // A gate must keep its dock alive for the whole sequence, including each wait for the
            // slot: dock reclamation would delete the worktree in between. The lease is held across
            // the sequence and released afterwards; the disk-lifecycle sweep and
            // DockService.ReclaimAsync both honor it.
            string? pinRefusal = await ApplySiblingPinsAsync(mission, token).ConfigureAwait(false);
            if (pinRefusal != null)
                return DefinitionOfDoneResult.Fail("sibling-pin", -1, BuildDiagnosticText(pinRefusal), DefinitionOfDoneFailureClassEnum.Infra);

            DockLeaseRegistry.Acquire(dock.Id);
            try
            {
                return await RunGateCommandsAsync(mission, profile, buildCommand, testCommand, worktreePath, token).ConfigureAwait(false);
            }
            finally
            {
                DockLeaseRegistry.Release(dock.Id);
            }
        }

        /// <summary>
        /// Describe the configuration this gate would apply to the mission. Uses the same skip rules and
        /// workflow-profile resolution as <see cref="EvaluateAsync"/>, but runs no command and reads no diff.
        /// </summary>
        /// <param name="mission">The mission to describe.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current effective configuration.</returns>
        public async Task<DefinitionOfDoneConfiguration> DescribeAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            DefinitionOfDoneConfiguration configuration = new DefinitionOfDoneConfiguration
            {
                GateActive = true,
                Enabled = _Settings.Enabled,
                AppliedPersonas = new List<string>(_Settings.AppliedPersonas ?? new List<string>()),
                PersonaApplies = IsPersonaApplicable(mission.Persona),
                DocOnlyMarkerPresent = HasDocOnlyMarker(mission.Description),
                ExpectedSkipReason = ResolveSkipReason(mission),
                RunRestoreBeforeBuild = _Settings.RunRestoreBeforeBuild,
                CommandTimeoutSeconds = _Settings.CommandTimeoutSeconds,
                CommandTimeoutSource = CommandTimeoutSettingsSource,
                VerifyDeclaredConsumers = _Settings.VerifyDeclaredConsumers,
                FailOnConsumerVerificationError = _Settings.FailOnConsumerVerificationError,
                RunConsumerTests = _Settings.RunConsumerTests,
                VerifyConsumersAfterLaterStages = _Settings.VerifyConsumersAfterLaterStages,
                RerunFailingConsumerClassesOnce = _Settings.RerunFailingConsumerClassesOnce,
                RerunFailingClassesOnce = _Settings.RerunFailingClassesOnce,
                DefaultConsumerTestTriggerPaths = new List<string>(_Settings.ConsumerTestTriggerPaths ?? new List<string>())
            };

            WorkflowProfile? profile = await ResolveProfileAsync(mission, token).ConfigureAwait(false);
            if (profile != null)
            {
                configuration.WorkflowProfileId = profile.Id;
                configuration.WorkflowProfileName = profile.Name;
                configuration.WorkflowProfileScope = profile.Scope;
                configuration.HasBuildCommand = !String.IsNullOrWhiteSpace(profile.BuildCommand);
                configuration.HasUnitTestCommand = !String.IsNullOrWhiteSpace(profile.UnitTestCommand);
                configuration.HasContainerlessUnitTestCommand = !String.IsNullOrWhiteSpace(profile.ContainerlessUnitTestCommand);
                configuration.CommandTimeoutSeconds = ResolveCommandTimeoutSeconds(profile);
                if (profile.GateCommandTimeoutSeconds.HasValue) configuration.CommandTimeoutSource = CommandTimeoutProfileSource;
            }

            configuration.MissingCommands = !configuration.HasBuildCommand && !configuration.HasUnitTestCommand;
            return configuration;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// The one definition of when the gate does not apply. Evaluation and description both call it.
        /// </summary>
        private string? ResolveSkipReason(Mission mission)
        {
            if (!_Settings.Enabled)
                return "DoD gate is disabled";

            if (!IsPersonaApplicable(mission.Persona))
                return "persona '" + (mission.Persona ?? "(none)") + "' is not in AppliedPersonas";

            if (HasDocOnlyMarker(mission.Description))
                return "mission description contains doc-only opt-out marker";

            return null;
        }

        /// <summary>
        /// Whether a mission the gate skips is a later pipeline stage that must still re-verify the
        /// declared consumers of the branch it continues. Only the persona rule may have skipped it: a
        /// disabled gate or a doc-only mission stays skipped.
        /// </summary>
        private bool AppliesLaterStageConsumerCheck(Mission mission)
        {
            if (!_Settings.Enabled || !_Settings.VerifyDeclaredConsumers || !_Settings.VerifyConsumersAfterLaterStages) return false;
            if (_Git == null) return false;
            if (IsPersonaApplicable(mission.Persona)) return false;
            if (HasDocOnlyMarker(mission.Description)) return false;
            return !String.IsNullOrWhiteSpace(mission.VesselId);
        }

        /// <summary>
        /// Re-verify the declared consumers for a later stage that committed to the branch. The stage's
        /// own change decides whether anything runs: a change to test files only cannot break a consumer,
        /// so it stays skipped. Any other change, or a change that cannot be read, runs the same consumer
        /// verification the applied stage ran, against the branch as this stage leaves it. A vessel with
        /// no declared consumer stays skipped with the persona reason.
        /// </summary>
        private async Task<DefinitionOfDoneResult> EvaluateLaterStageConsumersAsync(
            Mission mission,
            Dock dock,
            string? stageStartCommit,
            string skipReason,
            CancellationToken token)
        {
            string? worktreePath = dock.WorktreePath;
            if (String.IsNullOrWhiteSpace(worktreePath)) return DefinitionOfDoneResult.Skipped(skipReason);
            if (Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(worktreePath)
                && await HasDeclaredConsumersAsync(mission, token).ConfigureAwait(false))
                return DefinitionOfDoneResult.Fail("consumer-verification", -1,
                    BuildDiagnosticText("harbor_owned_consumer_verification_unsupported"), DefinitionOfDoneFailureClassEnum.Infra);

            Vessel? producer = await ReadVesselAsync(mission.TenantId, mission.VesselId!, token).ConfigureAwait(false);
            if (producer == null) return DefinitionOfDoneResult.Skipped(skipReason);

            List<Vessel> allVessels = await EnumerateVesselsAsync(mission.TenantId, token).ConfigureAwait(false);
            if (ConsumerVesselResolver.Resolve(producer.Id, producer.Name, allVessels).Count == 0)
                return DefinitionOfDoneResult.Skipped(skipReason);

            string? unreadReason = null;
            if (String.IsNullOrWhiteSpace(stageStartCommit))
            {
                unreadReason = "the stage start commit is not known";
            }
            else
            {
                try
                {
                    // Committed changes only: a dock also holds untracked files Armada writes for the
                    // captain (the briefing folder, runtime instruction files), and only commits land.
                    ChangedPathsRead stageRead = await _Git!.GetChangedFilePathsAgainstBaseAsync(worktreePath!, stageStartCommit!, token).ConfigureAwait(false);
                    if (stageRead == null || !stageRead.Available)
                        throw new InvalidOperationException(stageRead?.FormatReason() ?? "the git seam returned no changed-path result");
                    bool changedProduction = false;
                    foreach (string rawPath in stageRead.Paths)
                    {
                        if (String.IsNullOrWhiteSpace(rawPath)) continue;
                        if (!IsLikelyTestPath(rawPath.Replace('\\', '/'))) { changedProduction = true; break; }
                    }

                    if (!changedProduction)
                    {
                        return DefinitionOfDoneResult.Skipped(skipReason
                            + "; this stage changed no production file, so its declared consumers are not re-verified");
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    unreadReason = ex.GetType().Name + ": " + ex.Message;
                }
            }

            if (unreadReason != null)
            {
                _Logging.Warn(_Header + "re-verifying consumers for " + (mission.Persona ?? "(none)") + " mission " + mission.Id
                    + " because its own change could not be read: " + unreadReason);
            }
            else
            {
                _Logging.Info(_Header + (mission.Persona ?? "(none)") + " mission " + mission.Id
                    + " committed production changes after the applied stage; re-verifying declared consumers");
            }

            DockLeaseRegistry.Acquire(dock.Id);
            try
            {
                return await VerifyDeclaredConsumersAsync(mission, worktreePath!, token).ConfigureAwait(false);
            }
            finally
            {
                DockLeaseRegistry.Release(dock.Id);
            }
        }

        private async Task<DefinitionOfDoneResult> RunGateCommandsAsync(
            Mission mission,
            WorkflowProfile? profile,
            string? buildCommand,
            string? testCommand,
            string worktreePath,
            CancellationToken token)
        {
            if (Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(worktreePath)
                && await HasDeclaredConsumersAsync(mission, token).ConfigureAwait(false))
                return DefinitionOfDoneResult.Fail("consumer-verification", -1,
                    BuildDiagnosticText("harbor_owned_consumer_verification_unsupported"), DefinitionOfDoneFailureClassEnum.Infra);
            string? cleanHead = await ReadCleanHeadAsync(worktreePath, token).ConfigureAwait(false);
            DefinitionOfDoneResult? vesselPass = null;

            if (!String.IsNullOrWhiteSpace(buildCommand))
            {
                using IDisposable buildBudget = EnterCommandBudget(mission.VesselId ?? mission.Id, "build", profile);
                string effectiveBuild = _Settings.RunRestoreBeforeBuild ? EnsureRestore(buildCommand) : buildCommand;
                DefinitionOfDoneResult buildResult = await RunCommandAsync("build", effectiveBuild, worktreePath, token, limitProfile: profile).ConfigureAwait(false);
                if (!buildResult.Passed)
                    return buildResult;
                await CollectFullPassAsync(mission, "build", buildCommand!, worktreePath, cleanHead, profile, token).ConfigureAwait(false);
                vesselPass = buildResult;
            }

            if (!String.IsNullOrWhiteSpace(testCommand))
            {
                using IDisposable testBudget = EnterCommandBudget(mission.VesselId ?? mission.Id, "unit-test", profile);
                // A passing build cannot substitute for full test output if only an isolated retry passes.
                vesselPass = null;
                // Container pre-flight. Without a runtime, every container-backed fixture fails and the
                // gate spends its whole timeout proving the environment is missing. When the vessel has
                // declared a containerless variant, run that instead so the gate still verifies
                // everything that does not need containers rather than reporting a blanket failure.
                string selectedTest = testCommand!;
                string testLabel = "unit-test";
                if (!String.IsNullOrWhiteSpace(profile?.ContainerlessUnitTestCommand)
                    && _ContainerRuntimeProbe != null
                    && !Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(worktreePath)
                    && !await _ContainerRuntimeProbe.IsAvailableAsync(worktreePath, token).ConfigureAwait(false))
                {
                    selectedTest = profile!.ContainerlessUnitTestCommand!;
                    testLabel = "unit-test (containerless)";
                    _Logging.Warn(_Header + "no container runtime detected; running the vessel's containerless unit-test command");
                }

                string effectiveTest = _Settings.RunRestoreBeforeBuild ? EnsureRestore(selectedTest) : selectedTest;
                Regex? failedTestLinePattern = ResolveFailedTestLinePattern(profile, "vessel");
                DefinitionOfDoneResult testResult = await RunCommandAsync(testLabel, effectiveTest, worktreePath, token, failedTestLinePattern, limitProfile: profile).ConfigureAwait(false);
                testResult = await RetryPostTestStepOnceAsync("vessel", testLabel, effectiveTest, worktreePath, testResult, failedTestLinePattern, profile, token).ConfigureAwait(false);
                if (testResult.Passed)
                {
                    // This command ran in full; a pass from the isolated re-run below did not.
                    vesselPass = testResult;
                    await CollectFullPassAsync(mission, testLabel, selectedTest, worktreePath, cleanHead, profile, token).ConfigureAwait(false);
                }
                else
                {
                    // D15 flake_score: when the failure reads as a load flake or a known flaky family,
                    // re-run only the failing classes in isolation; the re-run's real result is the
                    // truth. A red that stays red here is returned unchanged. The model never marks it
                    // green — only a genuine passing isolated re-run can.
                    DefinitionOfDoneResult afterFlake = await MaybeRerunFlakyTestAsync(mission, profile, effectiveTest, worktreePath, testResult, token).ConfigureAwait(false);

                    // A test that loses a race under the loaded suite passes when its class runs alone, and
                    // a real defect fails again; the isolated re-run is the truth. Test Checks apply the same
                    // rule through IsolatedRerunPlan and record the full-run failure in their summary, so a
                    // race only the full suite shows is not hidden. Bounded to a few failing classes.
                    if (!afterFlake.Passed && ReferenceEquals(afterFlake, testResult) && _Settings.RerunFailingClassesOnce)
                        afterFlake = await RerunFailingClassesOnceAsync(mission, "vessel", profile, effectiveTest, worktreePath, testResult, testLabel, token).ConfigureAwait(false);
                    if (!afterFlake.Passed) return afterFlake;

                    // The isolated re-run passed: the failure was a flake. Fall through to the consumer
                    // verification the gate would have run had the suite passed the first time.
                }
            }

            // Keep the last full vessel command's proof only after every required consumer passes.
            // A consumer failure carries its own output; an isolated test retry supplies no full-suite proof.
            DefinitionOfDoneResult consumers = await VerifyDeclaredConsumersAsync(mission, worktreePath, token).ConfigureAwait(false);
            return consumers.Passed && String.IsNullOrEmpty(consumers.SkippedReason) && vesselPass != null
                ? vesselPass
                : consumers;
        }

        /// <summary>
        /// Read the worktree HEAD when it has no uncommitted tracked change, or null when it has one or cannot be read.
        /// </summary>
        private async Task<string?> ReadCleanHeadAsync(string worktreePath, CancellationToken token)
        {
            if (_Git == null) return null;
            try
            {
                if (await _Git.HasUncommittedTrackedChangesAsync(worktreePath, token).ConfigureAwait(false)) return null;
                string? head = await _Git.GetHeadCommitHashAsync(worktreePath, token).ConfigureAwait(false);
                return String.IsNullOrWhiteSpace(head) ? null : head.Trim();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "could not read a clean HEAD in " + worktreePath + ", so no command pass is recorded: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Keep a command that passed in full for this attempt, when the worktree was clean at the same HEAD
        /// before the gate's first command and after this one. The gate publishes it only if it passes.
        /// </summary>
        private async Task CollectFullPassAsync(Mission mission, string label, string command, string worktreePath, string? cleanHead, WorkflowProfile? profile, CancellationToken token)
        {
            GateSlot? slot = _GateSlot.Value;
            if (slot == null || cleanHead == null || String.IsNullOrWhiteSpace(mission.VesselId)) return;

            string? headAfter = await ReadCleanHeadAsync(worktreePath, token).ConfigureAwait(false);
            if (!String.Equals(cleanHead, headAfter, StringComparison.OrdinalIgnoreCase)) return;

            slot.FullPasses.Add(new DefinitionOfDoneCommandPass
            {
                MissionId = mission.Id,
                VesselId = mission.VesselId!,
                CommitHash = cleanHead,
                Label = label,
                Command = command,
                EnvironmentFingerprint = DefinitionOfDoneCommandPasses.FingerprintEnvironment(WithSiblingPins(profile?.EnvironmentVariables)),
                ExecutionBudgetTicks = ActiveExecutionBudget?.Limit.Ticks,
                CompletedUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Longest output silence before a time limit that still counts as a command writing output: a quarter of
        /// the command's limit, and never more than this. A command quieter than that at its limit reads as hung.
        /// </summary>
        public static readonly TimeSpan LiveOutputWindow = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether a command that hit its time limit was still writing output when the limit fired.
        /// </summary>
        /// <param name="outputQuiet">How long the command had written nothing at its limit; null when it wrote nothing.</param>
        /// <param name="limit">The command's time limit.</param>
        /// <returns>True when the last output came within <see cref="LiveOutputWindow"/> and a quarter of the limit.</returns>
        public static bool WasWritingOutputAtLimit(TimeSpan? outputQuiet, TimeSpan limit)
        {
            if (!outputQuiet.HasValue) return false;
            TimeSpan window = TimeSpan.FromTicks(Math.Min(LiveOutputWindow.Ticks, limit.Ticks / 4));
            return outputQuiet.Value <= window;
        }

        /// <summary>Source name of a command time limit read from the global settings.</summary>
        public const string CommandTimeoutSettingsSource = "settings";

        /// <summary>Source name of a command time limit read from the workflow profile.</summary>
        public const string CommandTimeoutProfileSource = "workflow_profile";

        /// <summary>
        /// Seconds a gate command run with the profile may take: the profile's <c>GateCommandTimeoutSeconds</c>
        /// when set, otherwise the global <c>CommandTimeoutSeconds</c>. Both are held to [30, 3600].
        /// </summary>
        /// <param name="profile">The profile the command comes from, or null.</param>
        /// <returns>The configured limit in seconds.</returns>
        private int ResolveCommandTimeoutSeconds(WorkflowProfile? profile)
        {
            int seconds = profile?.GateCommandTimeoutSeconds ?? _Settings.CommandTimeoutSeconds;
            return Math.Max(WorkflowProfile.MinimumGateCommandTimeoutSeconds, Math.Min(WorkflowProfile.MaximumGateCommandTimeoutSeconds, seconds));
        }

        /// <summary>
        /// How long a single gate command with the configured limit may run before it is cancelled.
        /// </summary>
        /// <remarks>
        /// The configured limit has a deliberate 30-second floor: a shorter production timeout would
        /// kill legitimate builds. That floor also made the "a hanging command is interrupted" test
        /// wait a real 30 seconds, which was the single slowest test in the suite. Overriding this
        /// seam lets that test prove the same behavior in about a second without lowering the floor
        /// for real captains.
        /// </remarks>
        /// <param name="configuredSeconds">The limit configured for the command, from its profile or the settings.</param>
        /// <returns>Timeout for one command.</returns>
        protected virtual TimeSpan ResolveCommandTimeout(int configuredSeconds)
        {
            return TimeSpan.FromSeconds(configuredSeconds);
        }

        /// <summary>
        /// Build every vessel that declares the mission's vessel as a sibling repository.
        /// </summary>
        /// <remarks>
        /// The producer's change is still unlanded here, which is the whole point: this is the
        /// last moment at which a public-API break can be attributed to the change that caused
        /// it rather than to whatever happens to build next.
        /// <para>
        /// Each consumer is materialized under a scratch root unique to this verification. That
        /// is deliberate and not merely tidy: shared sibling checkouts are reused rather than
        /// re-pointed when another dock already owns them, so a shared path could leave the
        /// consumer compiling against some other commit while reporting on this one. A private
        /// root cannot be reused by anyone, so what is built is always what was asked for.
        /// </para>
        /// <para>
        /// The producer is checked out detached at the mission branch; the consumer's other
        /// declared siblings take their default branches, because only the producer is under
        /// test. Consumers are built, not tested: a build catches the break that leaves a target
        /// branch red, while running every consumer's suite inside every producer gate would cost
        /// more wall time than the gate itself.
        /// </para>
        /// </remarks>
        private async Task<DefinitionOfDoneResult> VerifyDeclaredConsumersAsync(
            Mission mission,
            string producerWorktreePath,
            CancellationToken token)
        {
            if (!_Settings.VerifyDeclaredConsumers) return DefinitionOfDoneResult.Pass();

            // No git seam means no way to provision a consumer. Skipping is correct; failing would
            // block every gate in a host that simply did not wire the dependency.
            if (_Git == null) return DefinitionOfDoneResult.Pass();

            if (String.IsNullOrWhiteSpace(mission.VesselId)) return DefinitionOfDoneResult.Pass();

            Vessel? producer = await ReadVesselAsync(mission.TenantId, mission.VesselId!, token).ConfigureAwait(false);
            if (producer == null) return DefinitionOfDoneResult.Pass();

            List<Vessel> allVessels = await EnumerateVesselsAsync(mission.TenantId, token).ConfigureAwait(false);
            IReadOnlyList<ConsumerDeclaration> consumers =
                ConsumerVesselResolver.Resolve(producer.Id, producer.Name, allVessels);
            if (consumers.Count == 0) return DefinitionOfDoneResult.Pass();

            string? producerRef = !String.IsNullOrWhiteSpace(mission.BranchName)
                ? mission.BranchName
                : await _Git.GetHeadCommitHashAsync(producerWorktreePath, token).ConfigureAwait(false);

            if (String.IsNullOrWhiteSpace(producerRef))
            {
                return ConsumerVerificationError(
                    "consumer-verify",
                    "Could not determine the producer ref to verify consumers against.");
            }

            _Logging.Info(_Header + "verifying " + consumers.Count + " declared consumer(s) of vessel " + producer.Id);

            // The producer's changed paths decide whether each consumer's suite runs. Read them
            // once here rather than per consumer: the diff is a property of the producer's branch,
            // not of any one consumer.
            ChangedPathsRead producerChangedPaths =
                await ReadProducerChangedPathsAsync(producer, producerWorktreePath, token).ConfigureAwait(false);

            foreach (ConsumerDeclaration edge in consumers)
            {
                DefinitionOfDoneResult result = await VerifyOneConsumerAsync(
                    mission,
                    producer,
                    producerRef!,
                    edge,
                    producerChangedPaths,
                    token).ConfigureAwait(false);
                if (!result.Passed) return result;
            }

            return DefinitionOfDoneResult.Pass();
        }

        private async Task<DefinitionOfDoneResult> VerifyOneConsumerAsync(
            Mission mission,
            Vessel producer,
            string producerRef,
            ConsumerDeclaration edge,
            ChangedPathsRead producerChangedPaths,
            CancellationToken token)
        {
            Vessel consumer = edge.Consumer;
            string label = "consumer-build (" + consumer.Name + ")";
            SiblingRepo? producerSibling = null;

            WorkflowProfile? consumerProfile = await ResolveProfileForVesselAsync(consumer, token).ConfigureAwait(false);
            string? consumerBuild = consumerProfile?.BuildCommand;
            if (String.IsNullOrWhiteSpace(consumerBuild))
            {
                return ConsumerVerificationError(
                    label,
                    "Consumer vessel '" + consumer.Name + "' has no BuildCommand on its workflow profile, so its "
                    + "compilation against this change cannot be verified.");
            }

            string scratchRoot = Path.Combine(
                Path.GetTempPath(),
                "armada-consumer-verify",
                Guid.NewGuid().ToString("N"));

            List<string> createdWorktrees = new List<string>();
            List<string> artifactLinks = new List<string>();

            try
            {
                Directory.CreateDirectory(scratchRoot);

                string consumerWorktree = Path.Combine(scratchRoot, SafeDirName(consumer.Name));
                string? consumerRepo = ResolveVesselRepoPath(consumer);
                if (consumerRepo == null)
                {
                    return ConsumerVerificationError(
                        label,
                        "Consumer vessel '" + consumer.Name + "' has no LocalPath, so its repository cannot be located.");
                }

                string consumerBranch = ResolveDefaultBranch(consumer);

                await _Git.CreateWorktreeAsync(
                    consumerRepo, consumerWorktree, consumerBranch, consumerBranch, true, token).ConfigureAwait(false);
                createdWorktrees.Add(consumerWorktree);
                await LockConsumerWorktreeAsync(consumerWorktree, token).ConfigureAwait(false);

                Dictionary<string, string> provisionedRoots = new Dictionary<string, string>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                if (!String.IsNullOrWhiteSpace(consumer.WorkingDirectory))
                    provisionedRoots[consumer.WorkingDirectory!] = consumerWorktree;

                // Materialize every sibling the consumer declares. The producer takes the mission
                // ref; the rest take their declared defaults, because only the producer is under
                // test and pinning the others would verify a combination nobody is proposing.
                foreach (SiblingRepo sibling in consumer.GetSiblingRepos())
                {
                    if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;

                    Vessel? siblingVessel = await ResolveSiblingVesselAsync(sibling, allowNull: true, token).ConfigureAwait(false);
                    if (siblingVessel == null) continue;

                    bool isProducer = String.Equals(siblingVessel.Id, producer.Id, StringComparison.OrdinalIgnoreCase);
                    if (isProducer) producerSibling = sibling;
                    string siblingRef = isProducer
                        ? producerRef
                        : (!String.IsNullOrWhiteSpace(sibling.DefaultBranch) ? sibling.DefaultBranch! : ResolveDefaultBranch(siblingVessel));

                    string siblingPath = Path.GetFullPath(Path.Combine(consumerWorktree, sibling.RelativePath));
                    string? siblingRepo = ResolveVesselRepoPath(siblingVessel);
                    if (siblingRepo == null)
                    {
                        // The producer is the subject of this verification; without it the build
                        // would prove nothing, so report rather than run a misleading compile.
                        if (isProducer)
                        {
                            return ConsumerVerificationError(
                                label,
                                "Producer vessel '" + siblingVessel.Name + "' has no LocalPath, so it cannot be "
                                + "provisioned into consumer '" + consumer.Name + "' for verification.");
                        }

                        _Logging.Warn(_Header + "sibling " + siblingVessel.Name + " has no LocalPath; skipping it for consumer verification");
                        continue;
                    }

                    await _Git.CreateWorktreeAsync(
                        siblingRepo, siblingPath, siblingRef, siblingRef, true, token).ConfigureAwait(false);
                    createdWorktrees.Add(siblingPath);
                    if (!String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory))
                        provisionedRoots[siblingVessel.WorkingDirectory!] = siblingPath;
                    await LockConsumerWorktreeAsync(siblingPath, token).ConfigureAwait(false);
                    LinkSiblingArtifacts(sibling, siblingVessel, siblingPath, artifactLinks);
                }

                Dictionary<string, string> consumerEnvironment = RebaseConsumerEnvironment(consumerProfile!, provisionedRoots);
                // This resolved profile belongs to this verification only. Bind retries to the same
                // provisioned inputs as the first build and test commands.
                consumerProfile!.EnvironmentVariables = consumerEnvironment;
                string effective = _Settings.RunRestoreBeforeBuild ? EnsureRestore(consumerBuild!) : consumerBuild!;

                // A consumer build is a whole-solution build. Starting it on a busy host adds to the spike that makes
                // other commands time out, so it waits for a quiet host first. The wait is always named and never fails
                // the gate; at the maximum the build runs anyway.
                HostQuietWait quietWait = await _HostLoad.WaitForQuietHostAsync(
                    _Settings.ConsumerBuildLoadLimit, ConsumerBuildQuietReadings, token).ConfigureAwait(false);
                string loadLine = label + " " + quietWait.Describe();
                if (quietWait.EndedAtMaximum) _Logging.Warn(_Header + loadLine);
                else _Logging.Info(_Header + loadLine);

                DefinitionOfDoneResult buildResult;
                using (EnterCommandBudget(consumer.Id, "build", consumerProfile))
                    buildResult = await RunCommandAsync(label, effective, consumerWorktree, token,
                        environment: consumerEnvironment, limitProfile: consumerProfile).ConfigureAwait(false);

                if (!buildResult.Passed)
                {
                    _Logging.Warn(_Header + "consumer " + consumer.Name + " failed to build against this change");
                    buildResult.OutputTail = loadLine + Environment.NewLine + buildResult.OutputTail;
                    return buildResult;
                }

                // A build proves the consumer still compiles; it cannot prove the consumer still
                // behaves. When the producer change reaches a triggering path, run the consumer's
                // own suite against the same provisioned worktree before the branch may land.
                if (ShouldRunConsumerTests(producerSibling, producerChangedPaths, consumer.Name))
                {
                    DefinitionOfDoneResult testResult =
                        await RunConsumerTestsAsync(mission, consumer, consumerProfile, consumerWorktree, token).ConfigureAwait(false);
                    if (!testResult.Passed) return testResult;
                }

                return buildResult;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ConsumerVerificationError(
                    label,
                    "Could not prepare consumer '" + consumer.Name + "' for verification: " + ex.Message);
            }
            finally
            {
                // Links first: a worktree removal or a recursive delete must never reach the host
                // artifact trees the links point at.
                RemoveArtifactLinks(artifactLinks);

                foreach (string worktree in createdWorktrees)
                {
                    // A locked worktree refuses removal, so the lock taken at provisioning is released first.
                    try { await _Git!.UnlockWorktreeAsync(worktree, token).ConfigureAwait(false); }
                    catch (Exception ex) { _Logging.Debug(_Header + "consumer worktree unlock failed for " + worktree + ": " + ex.Message); }
                    try { await _Git!.RemoveWorktreeAsync(worktree, token).ConfigureAwait(false); }
                    catch (Exception ex) { _Logging.Debug(_Header + "consumer worktree cleanup failed for " + worktree + ": " + ex.Message); }
                }

                try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true); }
                catch (Exception ex) { _Logging.Debug(_Header + "consumer scratch cleanup failed for " + scratchRoot + ": " + ex.Message); }
            }
        }

        /// <summary>
        /// Rebase absolute profile paths from host checkouts into the consumer verification's provisioned
        /// trees. Match complete directory prefixes, longest first, so a nested dependency takes its own
        /// candidate tree. Relative paths, path lists and unrelated values keep their configured meaning.
        /// </summary>
        private static Dictionary<string, string> RebaseConsumerEnvironment(
            WorkflowProfile profile,
            IReadOnlyDictionary<string, string> provisionedRoots)
        {
            Dictionary<string, string> environment = MergeCommandEnvironment(profile, null);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            List<KeyValuePair<string, string>> roots = provisionedRoots
                .Where(pair => Path.IsPathFullyQualified(pair.Key))
                .Select(pair => new KeyValuePair<string, string>(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Key)), pair.Value))
                .Where(pair => !String.Equals(pair.Key, Path.GetPathRoot(pair.Key), comparison))
                .OrderByDescending(pair => pair.Key.Length)
                .ToList();
            foreach (string key in environment.Keys.ToList())
            {
                string value = environment[key];
                if (!Path.IsPathFullyQualified(value) || value.IndexOf(Path.PathSeparator) >= 0
                    || value.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0) continue;
                string path = Path.GetFullPath(value);
                foreach (KeyValuePair<string, string> root in roots)
                {
                    string prefix = Path.EndsInDirectorySeparator(root.Key) ? root.Key : root.Key + Path.DirectorySeparatorChar;
                    if (String.Equals(path, root.Key, comparison))
                    {
                        environment[key] = root.Value;
                        break;
                    }
                    if (path.StartsWith(prefix, comparison))
                    {
                        environment[key] = Path.Combine(root.Value, path.Substring(prefix.Length));
                        break;
                    }
                }
            }
            return environment;
        }

        /// <summary>
        /// Lock a provisioned consumer-verification worktree for the length of the verification. The
        /// repositories these worktrees belong to are shared with docks, Checks and processes outside this
        /// one, and the scratch directory is visible only where the admiral runs: a <c>git worktree prune</c>
        /// run anywhere that cannot see it would otherwise delete the registration mid-run, and every git
        /// call inside the worktree, the consumer's own source checks among them, would then fail. A lock
        /// that cannot be taken is logged and the verification goes on unlocked.
        /// </summary>
        private async Task LockConsumerWorktreeAsync(string worktreePath, CancellationToken token)
        {
            try
            {
                await _Git!.LockWorktreeAsync(worktreePath, ConsumerVerifyLockReason, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not lock consumer verification worktree " + worktreePath + "; it stays unlocked: " + ex.Message);
            }
        }

        /// <summary>
        /// Link a sibling's git-ignored extraction artifact directories (for example a decompiled source
        /// tree) from the sibling vessel's host working directory into its verification worktree. A
        /// mission dock copies the same directories from the same place, so the consumer suite reads
        /// the trees it reads in a dock; without them every tree-dependent test fails and the gate
        /// blames the producer. The verification only reads the trees, so a link replaces the copy. A
        /// partial tree the worktree already tracks at that path is moved aside first. A missing source
        /// is logged and skipped, as a dock does.
        /// </summary>
        /// <param name="sibling">The consumer's sibling declaration.</param>
        /// <param name="siblingVessel">The sibling vessel.</param>
        /// <param name="siblingWorktree">The sibling's verification worktree.</param>
        /// <param name="links">Receives each link created, for removal.</param>
        internal void LinkSiblingArtifacts(SiblingRepo sibling, Vessel siblingVessel, string siblingWorktree, List<string> links)
        {
            if (sibling?.ExtractionArtifactPaths == null || sibling.ExtractionArtifactPaths.Count == 0) return;
            if (siblingVessel == null || String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory)) return;

            foreach (string artifactPath in sibling.ExtractionArtifactPaths)
            {
                if (String.IsNullOrWhiteSpace(artifactPath)) continue;
                string source = Path.GetFullPath(Path.Combine(siblingVessel.WorkingDirectory, artifactPath));
                string destination = Path.GetFullPath(Path.Combine(siblingWorktree, artifactPath));
                try
                {
                    if (!Directory.Exists(source))
                    {
                        _Logging.Warn(_Header + "extraction artifact source absent for consumer verification (vessel "
                            + siblingVessel.Name + ", path " + artifactPath + "); its tree-dependent tests will not find it");
                        continue;
                    }
                    // A deobfuscator commits a few files under its artifact directory, so the worktree
                    // already holds a partial tree there. The host working directory holds the whole
                    // tree, tracked files included; move the partial one aside and link the whole one,
                    // as a dock replaces it with a full copy. The worktree is discarded after the run.
                    if (File.Exists(destination)) continue;
                    if (Directory.Exists(destination))
                    {
                        if (new DirectoryInfo(destination).LinkTarget != null) continue;
                        Directory.Move(destination, destination + ".tracked-" + Guid.NewGuid().ToString("N"));
                    }

                    string? parent = Path.GetDirectoryName(destination);
                    if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    Directory.CreateSymbolicLink(destination, source);
                    links.Add(destination);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not link extraction artifacts for consumer verification (vessel "
                        + siblingVessel.Name + ", path " + artifactPath + "): " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Remove the artifact links created for a verification. Only the link is removed; the tree it
        /// points at is never touched.
        /// </summary>
        /// <param name="links">Links created by <see cref="LinkSiblingArtifacts"/>.</param>
        internal void RemoveArtifactLinks(List<string> links)
        {
            foreach (string link in links)
            {
                try
                {
                    FileSystemInfo info = new DirectoryInfo(link);
                    if (info.LinkTarget == null) continue;
                    File.Delete(link);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not remove artifact link " + link + ": " + ex.Message);
                }
            }
            links.Clear();
        }

        /// <summary>
        /// Report a verification that could not be carried out, as distinct from a consumer that
        /// genuinely failed to compile.
        /// </summary>
        private DefinitionOfDoneResult ConsumerVerificationError(string label, string message)
        {
            if (_Settings.FailOnConsumerVerificationError)
            {
                _Logging.Warn(_Header + message);
                return DefinitionOfDoneResult.Fail(label, -1, BuildDiagnosticText(message), DefinitionOfDoneFailureClassEnum.Infra);
            }

            // Say it out loud. A verification that silently did not happen is indistinguishable
            // from one that passed, and that is the failure this whole step exists to prevent.
            _Logging.Warn(_Header + "consumer verification incomplete: " + message);
            return DefinitionOfDoneResult.Pass();
        }

        /// <summary>
        /// Read the producer's changed paths against its default branch, for deciding whether a
        /// consumer suite must run. A read that fails, including a git seam that throws, is
        /// returned as unavailable, never as an empty change.
        /// </summary>
        private async Task<ChangedPathsRead> ReadProducerChangedPathsAsync(
            Vessel producer,
            string producerWorktreePath,
            CancellationToken token)
        {
            if (_Git == null) return ChangedPathsRead.Unavailable("no git seam to read the producer change");
            string baseBranch = ResolveDefaultBranch(producer);
            try
            {
                ChangedPathsRead read = await _Git.GetChangedFilePathsAgainstBaseAsync(producerWorktreePath, baseBranch, token).ConfigureAwait(false);
                return read ?? ChangedPathsRead.Unavailable("the git seam returned no changed-path result");
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                return ChangedPathsRead.Unavailable(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Decide whether a consumer's unit-test suite runs for this producer change. It runs when
        /// consumer-test verification is enabled, trigger prefixes are configured, and either at
        /// least one changed producer path is a non-test file under a trigger prefix or the changed
        /// paths could not be read. An unreadable change may reach any trigger, so the suite runs
        /// and the named reason is logged. The prefixes come from the producer's own sibling
        /// declaration when it lists any, otherwise from the gate settings default.
        /// </summary>
        private bool ShouldRunConsumerTests(SiblingRepo? producerSibling, ChangedPathsRead producerChangedPaths, string consumerName)
        {
            if (!_Settings.RunConsumerTests) return false;

            IReadOnlyList<string>? triggers =
                producerSibling?.ConsumerTestTriggerPaths != null && producerSibling.ConsumerTestTriggerPaths.Count > 0
                    ? producerSibling.ConsumerTestTriggerPaths
                    : _Settings.ConsumerTestTriggerPaths;
            if (triggers == null || triggers.Count == 0) return false;

            if (!producerChangedPaths.Available)
            {
                _Logging.Warn(_Header + "running consumer tests for " + consumerName
                    + " because the producer change could not be read: " + producerChangedPaths.FormatReason());
                return true;
            }

            foreach (string rawPath in producerChangedPaths.Paths)
            {
                if (String.IsNullOrWhiteSpace(rawPath)) continue;
                string path = rawPath.Replace('\\', '/');
                if (IsLikelyTestPath(path)) continue;

                foreach (string trigger in triggers)
                {
                    if (String.IsNullOrWhiteSpace(trigger)) continue;
                    string prefix = NormalizeTriggerPrefix(trigger);
                    if (prefix.Length == 0) continue;
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        private static string NormalizeTriggerPrefix(string trigger)
        {
            string prefix = trigger.Replace('\\', '/').Trim();
            if (prefix.StartsWith("./", StringComparison.Ordinal)) prefix = prefix.Substring(2);
            return prefix;
        }

        /// <summary>
        /// Whether a repository-relative path names a test file rather than production source, so a
        /// change to it alone does not trigger a consumer-test run. A path is a test path when any
        /// segment is a test project (ends with ".Tests" or ".Test") or a "test"/"tests" directory.
        /// </summary>
        private static bool IsLikelyTestPath(string path)
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                if (segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                    || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (String.Equals(segment, "test", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Run a consumer's unit-test suite in its provisioned worktree. A failure is reported with
        /// a named reason distinct from a build break, so a broken consumer test can never be read
        /// as a compilation failure.
        /// </summary>
        private async Task<DefinitionOfDoneResult> RunConsumerTestsAsync(
            Mission mission,
            Vessel consumer,
            WorkflowProfile? consumerProfile,
            string consumerWorktree,
            CancellationToken token)
        {
            string? testCommand = consumerProfile?.UnitTestCommand;
            if (String.IsNullOrWhiteSpace(testCommand))
            {
                return ConsumerVerificationError(
                    "consumer-tests (" + consumer.Name + ")",
                    "Consumer vessel '" + consumer.Name + "' has no UnitTestCommand on its workflow profile, so its "
                    + "test suite cannot be run for a change that can break it.");
            }

            using IDisposable consumerBudget = EnterCommandBudget(consumer.Id, "unit-test", consumerProfile);
            string selectedTest = testCommand!;
            string logLabel = "consumer-tests (" + consumer.Name + ")";

            // Mirror the producer's container pre-flight: without a runtime, container-backed
            // fixtures fail for the environment, so a declared containerless variant runs instead.
            if (!String.IsNullOrWhiteSpace(consumerProfile?.ContainerlessUnitTestCommand)
                && _ContainerRuntimeProbe != null
                && !await _ContainerRuntimeProbe.IsAvailableAsync(consumerWorktree, token).ConfigureAwait(false))
            {
                selectedTest = consumerProfile!.ContainerlessUnitTestCommand!;
                _Logging.Warn(_Header + "no container runtime detected; running consumer '" + consumer.Name + "' containerless unit-test command");
            }

            string effective = _Settings.RunRestoreBeforeBuild ? EnsureRestore(selectedTest) : selectedTest;
            Regex? failedTestLinePattern = ResolveFailedTestLinePattern(consumerProfile, "consumer " + consumer.Name);
            DefinitionOfDoneResult result = await RunCommandAsync(logLabel, effective, consumerWorktree, token, failedTestLinePattern,
                environment: consumerProfile!.EnvironmentVariables, limitProfile: consumerProfile).ConfigureAwait(false);
            result = await RetryPostTestStepOnceAsync("consumer " + consumer.Name, logLabel, effective, consumerWorktree, result, failedTestLinePattern, consumerProfile, token).ConfigureAwait(false);

            if (!result.Passed && result.FailureClass == DefinitionOfDoneFailureClassEnum.PostTestStep)
            {
                // Every consumer test passed; only a step after them failed, twice. The label names that,
                // so neither recovery nor an operator reads it as a failing test in the consumer repository.
                _Logging.Warn(_Header + "consumer " + consumer.Name + " tests passed but a step after them failed twice");
                result.CommandLabel = DefinitionOfDoneFailureClassifier.ConsumerPostTestStepFailedLabelPrefix + consumer.Name;
                return result;
            }

            if (!result.Passed)
            {
                // A consumer suite has load-sensitive tests of its own. Score the red and, when the model
                // recommends it, re-run only the failing classes in the same worktree, as the producer's
                // own suite does. The re-run is the truth; a red that stays red keeps its consumer label.
                DefinitionOfDoneResult suiteResult = result;
                result = await MaybeRerunFlakyTestAsync(mission, consumerProfile, effective, consumerWorktree, result, token,
                    logLabel + " (flake re-run)", consumer.Id).ConfigureAwait(false);

                // The producer did not write the consumer's tests, so a consumer red is re-run once in
                // isolation whatever the model says: a break the producer caused fails again alone, and a
                // consumer test that loses a race under the loaded suite passes alone. The isolated
                // re-run is the truth either way. Bounded to a few failing classes.
                if (!result.Passed && ReferenceEquals(result, suiteResult) && _Settings.RerunFailingConsumerClassesOnce)
                    result = await RerunFailingClassesOnceAsync(mission, "consumer " + consumer.Name, consumerProfile, effective, consumerWorktree, result, logLabel, token).ConfigureAwait(false);
            }

            if (!result.Passed)
            {
                _Logging.Warn(_Header + "consumer " + consumer.Name + " test suite failed against this change");
                result.CommandLabel = DefinitionOfDoneFailureClassifier.ConsumerTestsFailedLabelPrefix + consumer.Name;
            }

            return result;
        }

        /// <summary>
        /// Run a test command once more when its first run failed as
        /// <see cref="DefinitionOfDoneFailureClassEnum.PostTestStep"/>: every test passed and a step after
        /// them (a wrapper's verification, a cleanup) failed. That step reads the host, not the change, so
        /// one transient fault there must not fail the gate. The second run is the result; any other
        /// failure class is returned unchanged.
        /// </summary>
        /// <param name="subject">Whose suite ran, for the log: "vessel" or "consumer &lt;name&gt;".</param>
        /// <param name="label">Command label of the first run.</param>
        /// <param name="command">The test command that ran.</param>
        /// <param name="workingDir">Directory the command ran in.</param>
        /// <param name="first">Result of the first run.</param>
        /// <param name="failedTestLinePattern">The profile's failing-test line pattern, if any.</param>
        /// <param name="token">Cancellation token.</param>
        private async Task<DefinitionOfDoneResult> RetryPostTestStepOnceAsync(
            string subject,
            string label,
            string command,
            string workingDir,
            DefinitionOfDoneResult first,
            Regex? failedTestLinePattern,
            WorkflowProfile? limitProfile,
            CancellationToken token)
        {
            if (first.Passed || first.FailureClass != DefinitionOfDoneFailureClassEnum.PostTestStep) return first;

            _Logging.Warn(_Header + subject + " tests passed but the command failed after them (exit " + first.ExitCode
                + "); running it once more");
            DefinitionOfDoneResult retry = await RunCommandAsync(label + " (post-test step retry)", command, workingDir, token, failedTestLinePattern, limitProfile: limitProfile).ConfigureAwait(false);
            _Logging.Info(_Header + subject + " post-test step retry passed=" + retry.Passed);
            if (!retry.Passed && retry.FailureClass == DefinitionOfDoneFailureClassEnum.PostTestStep)
            {
                retry.CommandLabel = label;
                retry.OutputTail = "The command was run twice; both runs passed every test and failed in the step after them."
                    + (String.IsNullOrEmpty(retry.OutputTail) ? String.Empty : "\n" + retry.OutputTail);
            }
            return retry;
        }

        /// <summary>
        /// Re-run a suite's failing tests once in isolation and return that result. Returns the original
        /// failing result, with the reason written at the top of its output, when the failure is not a test
        /// failure, the failing names are missing or overflowed, or <see cref="IsolatedRerunPlan"/> names a
        /// reason no isolated command can run. A re-run that fails again carries the host load it ran under, because
        /// it runs on the same host as everything else: a fail alone on a busy host does not rule out a load race.
        /// </summary>
        /// <param name="mission">The mission whose gate runs, so its own hold on the host command slot is not counted
        /// as other work.</param>
        /// <param name="subject">Whose suite failed, for the log: "vessel" or "consumer &lt;name&gt;".</param>
        /// <param name="profile">The workflow profile of the suite that failed.</param>
        private async Task<DefinitionOfDoneResult> RerunFailingClassesOnceAsync(
            Mission mission,
            string subject,
            WorkflowProfile? profile,
            string testCommand,
            string worktreePath,
            DefinitionOfDoneResult testResult,
            string logLabel,
            CancellationToken token)
        {
            IsolatedRerunPlan plan = IsolatedRerunPlan.ForFailedRun(
                profile, testCommand, testResult.FailureClass, testResult.FailedTestNames, testResult.FailedTestNamesOverflow);
            if (plan.NotTestFailure) return testResult;
            if (plan.Command == null)
                return NoteNoRerun(testResult, subject, plan.NotRerunReason ?? IsolatedRerunPlan.NoCommandReason);

            _Logging.Info(_Header + "re-running " + plan.Count + " failing " + plan.Unit + " of " + subject + " in isolation"
                + (plan.FromProfile ? " through the profile's IsolatedTestRerunCommand" : String.Empty));
            // The re-run reads its failing tests with the same profile pattern as the run it repeats, so its
            // failure text names them even when the runner prints them early in a long output.
            Regex? failedTestLinePattern = ResolveFailedTestLinePattern(profile, subject);
            DefinitionOfDoneResult rerun = Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(worktreePath)
                ? await RunHarborRerunAsync(logLabel + " (isolated re-run)", profile, plan, testResult, worktreePath, failedTestLinePattern, token).ConfigureAwait(false)
                : await RunIsolatedRerunAsync(logLabel + " (isolated re-run)", plan.Command, worktreePath, MergeCommandEnvironment(profile, plan.Environment), failedTestLinePattern, token).ConfigureAwait(false);
            _Logging.Info(_Header + subject + " isolated re-run passed=" + rerun.Passed);
            if (!rerun.Passed)
            {
                string load = await DescribeIsolatedRerunHostLoadAsync(mission, token).ConfigureAwait(false);
                _Logging.Info(_Header + subject + " isolated re-run failed; " + load);
                rerun.OutputTail = "Isolated re-run of " + plan.Count + " failing " + plan.Unit + " under the current host load also failed. "
                    + load
                    + (String.IsNullOrEmpty(rerun.OutputTail) ? String.Empty : "\n" + rerun.OutputTail);
            }

            return rerun;
        }

        /// <summary>
        /// Describe the host load an isolated re-run ran under, from figures the admiral already holds: the load
        /// averages and available memory, the other definition-of-done gates, the other holders and the waiters
        /// of the host command slot, and the working captains. The isolated re-run shares the host with all of
        /// them, so the text says the failure may still be load-related whenever the host was busy, and never
        /// that a load race is ruled out.
        /// </summary>
        /// <param name="mission">The mission whose gate runs; its own gate and slot hold are not counted.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One or two sentences: the load figures, then what they allow the reader to conclude.</returns>
        private async Task<string> DescribeIsolatedRerunHostLoadAsync(Mission mission, CancellationToken token)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            HostLoadReading reading = _HostLoad.Read();
            HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
            int otherHolders = slot.Holders.Count(h => !String.Equals(h.Key, mission.Id, StringComparison.Ordinal));
            int otherGates = DefinitionOfDoneGateRuns.Snapshot().Count(g => !String.Equals(g.Key, mission.Id, StringComparison.Ordinal));

            string captains;
            try
            {
                List<Captain> working = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);
                captains = working.Count.ToString(inv);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "working captain count for the isolated re-run load could not be read: " + ex.GetType().Name + ": " + ex.Message);
                captains = "unknown (" + ex.GetType().Name + ")";
            }

            string Format(double? value) => value.HasValue ? value.Value.ToString("0.00", inv) : "unknown";
            string memory = reading.AvailableMemoryMb.HasValue ? reading.AvailableMemoryMb.Value.ToString(inv) + " MB" : "unknown";
            string figures = "Host load at the re-run: load average "
                + Format(reading.LoadAverage1Min) + "/" + Format(reading.LoadAverage5Min) + "/" + Format(reading.LoadAverage15Min)
                + " (1/5/15 min) on " + reading.LogicalCores.ToString(inv) + " CPUs, MemAvailable " + memory
                + ", " + otherGates.ToString(inv) + " other definition-of-done gate(s) running or waiting"
                + ", host command slot " + otherHolders.ToString(inv) + " other holder(s) and " + slot.Waiters.Count.ToString(inv) + " waiter(s)"
                + ", " + captains + " captain(s) working.";

            double busyAbove = HostLoadMonitor.RecoveredThreshold(reading.LogicalCores);
            bool loadKnown = reading.LoadAverage1Min.HasValue || reading.LoadAverage5Min.HasValue || reading.LoadAverage15Min.HasValue;
            bool loadBusy = (reading.LoadAverage1Min ?? 0) > busyAbove || (reading.LoadAverage5Min ?? 0) > busyAbove || (reading.LoadAverage15Min ?? 0) > busyAbove;
            bool memoryBusy = slot.MemoryFloorMb > 0 && reading.AvailableMemoryMb.HasValue && reading.AvailableMemoryMb.Value < slot.MemoryFloorMb;
            if (loadBusy || memoryBusy || otherHolders > 0)
                return figures + " The host was busy, so the failure may still be load-related; re-run the failing tests on a quiet host before treating it as a defect.";
            if (!loadKnown)
                return figures + " The load average is unknown, so a load-related failure cannot be ruled out.";
            return figures + " The host was not busy at the re-run, so load is an unlikely cause; the failure points to a defect or an ordering race in the tests.";
        }

        private DefinitionOfDoneResult NoteNoRerun(DefinitionOfDoneResult testResult, string subject, string reason)
        {
            _Logging.Info(_Header + subject + " red not re-run in isolation: " + reason + "; the red stands");
            testResult.OutputTail = "Not re-run in isolation: " + reason + "."
                + (String.IsNullOrEmpty(testResult.OutputTail) ? String.Empty : "\n" + testResult.OutputTail);
            return testResult;
        }

        /// <summary>
        /// D15 <c>flake_score</c>: score a red unit-test result and, when the model recommends it, run an
        /// isolated class-filtered re-run whose real result becomes the truth. Returns the original
        /// failing result unchanged when the adapter is absent, the failure is not a test failure, the
        /// model does not recommend a re-run, or an isolated command could not be formed. Never marks a
        /// red result green on its own: only a genuine passing isolated re-run does.
        /// </summary>
        private async Task<DefinitionOfDoneResult> MaybeRerunFlakyTestAsync(
            Mission mission,
            WorkflowProfile? profile,
            string testCommand,
            string worktreePath,
            DefinitionOfDoneResult testResult,
            CancellationToken token,
            string rerunLabel = "unit-test (flake re-run)",
            string? historyVesselId = null)
        {
            if (_FlakeScoreAdapter == null) return testResult;
            if (testResult.FailureClass != DefinitionOfDoneFailureClassEnum.TestFail) return testResult;

            // Only a complete, non-overflowed set of failing names can be isolated into a filter.
            if (testResult.FailedTestNames == null || testResult.FailedTestNames.Count == 0 || testResult.FailedTestNamesOverflow)
                return testResult;

            // The flake-score re-run has no class bound of its own; a profile command is bounded by name count.
            IsolatedRerunPlan plan = IsolatedRerunPlan.Build(profile, testCommand, testResult.FailedTestNames, IsolatedRerunPlan.MaxCount, boundClasses: false);
            if (plan.NothingToRerun) return testResult;

            bool crossBranch;
            try
            {
                // A consumer failure is looked up in the consumer's own check history: that is where the
                // same test failing on another branch would have been recorded.
                Mission historyMission = mission;
                if (!String.IsNullOrWhiteSpace(historyVesselId))
                    historyMission = new Mission { Id = mission.Id, TenantId = mission.TenantId, VesselId = historyVesselId };
                crossBranch = await HasRecentCrossBranchFailureAsync(historyMission, testResult.FailedTestNames, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "flake_score cross-branch history lookup failed, treating as none: " + ex.Message);
                crossBranch = false;
            }

            FlakeScoreDecisionInput input = new FlakeScoreDecisionInput
            {
                Mission = mission,
                FailingTestNames = testResult.FailedTestNames,
                AssertionLines = testResult.OutputTail ?? String.Empty,
                TouchedFiles = new List<string>(),
                SameTestFailedElsewhere24h = crossBranch,
                RuleClass = DefinitionOfDoneFailureClassEnum.TestFail
            };

            FlakeScoreVerdict verdict = await _FlakeScoreAdapter.DecideAsync(input, FlakeScoreVerdict.NoRerun(), token).ConfigureAwait(false);
            if (!verdict.RerunRecommended) return testResult;

            if (plan.Command == null)
            {
                _Logging.Info(_Header + "flake_score recommended a re-run but " + (plan.NotRerunReason ?? IsolatedRerunPlan.NoCommandReason) + "; the red stands");
                return testResult;
            }

            _Logging.Info(_Header + "flake_score " + verdict.Outcome + ": re-running " + plan.Count + " failing " + plan.Unit + " in isolation");
            Regex? rerunPattern = ResolveFailedTestLinePattern(profile, "flake re-run");
            DefinitionOfDoneResult rerunResult = Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(worktreePath)
                ? await RunHarborRerunAsync(rerunLabel, profile, plan, testResult, worktreePath, rerunPattern, token).ConfigureAwait(false)
                : await RunIsolatedRerunAsync(
                    rerunLabel, plan.Command, worktreePath, MergeCommandEnvironment(profile, plan.Environment), rerunPattern, token).ConfigureAwait(false);

            // Record BOTH results: the original red and the isolated re-run. The re-run is the truth.
            _Logging.Info(_Header + "flake_score re-run outcome passed=" + rerunResult.Passed
                + " (original failure class=" + testResult.FailureClass + ", label=" + testResult.CommandLabel + ")");
            return rerunResult;
        }

        /// <summary>
        /// Test-only hook onto one consumer suite run, so its flake re-run can be proved with a scripted
        /// failing command and a stubbed isolated re-run (<see cref="RunIsolatedRerunAsync"/>).
        /// </summary>
        internal Task<DefinitionOfDoneResult> EvaluateConsumerTestsForTestAsync(
            Mission mission,
            Vessel consumer,
            WorkflowProfile consumerProfile,
            string consumerWorktree,
            CancellationToken token)
        {
            return RunConsumerTestsAsync(mission, consumer, consumerProfile, consumerWorktree, token);
        }

        /// <summary>
        /// Test-only hook onto the D15 flake re-run decision path, so the re-run-is-truth behavior can be
        /// proved without executing a real test command (the isolated re-run itself is stubbed by
        /// overriding <see cref="RunIsolatedRerunAsync"/>).
        /// </summary>
        internal Task<DefinitionOfDoneResult> EvaluateFlakeRerunForTestAsync(
            Mission mission,
            string testCommand,
            string worktreePath,
            DefinitionOfDoneResult testResult,
            CancellationToken token,
            WorkflowProfile? profile = null)
        {
            return MaybeRerunFlakyTestAsync(mission, profile, testCommand, worktreePath, testResult, token);
        }

        /// <summary>
        /// Run one isolated flake re-run command. A seam so a test can prove the re-run-is-truth behavior
        /// without executing a real test command; production runs the command in the dock worktree.
        /// </summary>
        /// <param name="label">The command label.</param>
        /// <param name="command">The isolated command: a class-filtered dotnet test command or the profile's IsolatedTestRerunCommand.</param>
        /// <param name="worktreePath">The dock worktree.</param>
        /// <param name="environment">Extra environment for the command, such as the failing names for a profile command.</param>
        /// <param name="failedTestLinePattern">The suite profile's failing-test line pattern, or null for the built-in matchers.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The re-run result.</returns>
        protected virtual Task<DefinitionOfDoneResult> RunIsolatedRerunAsync(
            string label,
            string command,
            string worktreePath,
            IReadOnlyDictionary<string, string> environment,
            Regex? failedTestLinePattern,
            CancellationToken token)
        {
            return RunCommandAsync(label, command, worktreePath, token, failedTestLinePattern, environment);
        }

        /// <summary>
        /// Whether any of the failing test names failed on another branch (a different mission) in the
        /// last 24 hours. Best-effort state for the D15 model; a lookup failure reads as "no". A seam so
        /// a test can supply history without a database.
        /// </summary>
        /// <param name="mission">The mission whose failure is being scored.</param>
        /// <param name="failingTestNames">The failing test names to look for.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a matching cross-branch failure was found.</returns>
        protected virtual async Task<bool> HasRecentCrossBranchFailureAsync(Mission mission, IReadOnlyList<string> failingTestNames, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VesselId) || failingTestNames.Count == 0) return false;

            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = mission.TenantId,
                VesselId = mission.VesselId,
                Status = CheckRunStatusEnum.Failed,
                FromUtc = DateTime.UtcNow.AddHours(-24),
                PageNumber = 1,
                PageSize = 50
            };

            EnumerationResult<CheckRun> recent = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (CheckRun run in recent.Objects)
            {
                if (String.Equals(run.MissionId, mission.Id, StringComparison.Ordinal)) continue;
                string haystack = (run.Output ?? String.Empty) + "\n" + (run.Summary ?? String.Empty);
                if (haystack.Length == 0) continue;
                foreach (string name in failingTestNames)
                {
                    if (!String.IsNullOrWhiteSpace(name) && haystack.Contains(name, StringComparison.Ordinal)) return true;
                }
            }

            return false;
        }

        private async Task<Vessel?> ReadVesselAsync(string? tenantId, string vesselId, CancellationToken token)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(tenantId)
                    ? await _Database.Vessels.ReadAsync(tenantId, vesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "vessel " + vesselId + " could not be read (database error, not a missing vessel): " + ex.Message);
                return null;
            }
        }

        private async Task<List<Vessel>> EnumerateVesselsAsync(string? tenantId, CancellationToken token)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(tenantId)
                    ? await _Database.Vessels.EnumerateAsync(tenantId, token).ConfigureAwait(false)
                    : await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "vessels could not be enumerated (database error); consumer builds for this change are not checked: " + ex.Message);
                return new List<Vessel>();
            }
        }

        private async Task<Vessel?> ResolveSiblingVesselAsync(SiblingRepo sibling, bool allowNull, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(sibling.VesselRef)) return null;

            Vessel? vessel = null;
            try { vessel = await _Database.Vessels.ReadAsync(sibling.VesselRef!, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "sibling vessel " + sibling.VesselRef + " could not be read by id (database error): " + ex.Message);
            }
            if (vessel == null)
            {
                try { vessel = await _Database.Vessels.ReadByNameAsync(sibling.VesselRef!, token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "sibling vessel " + sibling.VesselRef + " could not be read by name (database error): " + ex.Message);
                }
            }
            return vessel;
        }

        /// <summary>
        /// Locate a vessel's bare repository, or null when the record does not say where it is.
        /// </summary>
        /// <remarks>
        /// Only the vessel's own <see cref="Vessel.LocalPath"/> is consulted. Guessing a path from
        /// the vessel name would produce a plausible directory that may not be the repository, and
        /// a verification that silently built the wrong tree is worse than one that reports it
        /// could not run.
        /// </remarks>
        private static string? ResolveVesselRepoPath(Vessel vessel)
        {
            if (String.IsNullOrWhiteSpace(vessel.LocalPath)) return null;
            return Path.GetFullPath(vessel.LocalPath!);
        }

        private static string ResolveDefaultBranch(Vessel vessel)
        {
            return !String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? vessel.DefaultBranch! : "main";
        }

        private static string SafeDirName(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "consumer";
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name;
        }

        private async Task<WorkflowProfile?> ResolveProfileForVesselAsync(Vessel vessel, CancellationToken token)
        {
            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = vessel.TenantId,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0) return null;

            WorkflowProfile? match = ChooseBestFromScope(
                candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(p.VesselId, vessel.Id, StringComparison.Ordinal)).ToList());
            if (match != null) return match;

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestFromScope(
                    candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(p.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null) return match;
            }

            return ChooseBestFromScope(candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Global).ToList());
        }

        private bool IsPersonaApplicable(string? persona)
        {
            if (_Settings.AppliedPersonas == null || _Settings.AppliedPersonas.Count == 0)
                return false;
            return _Settings.AppliedPersonas.Exists(p =>
                String.Equals(p, persona, StringComparison.OrdinalIgnoreCase));
        }

        private bool HasDocOnlyMarker(string? description)
        {
            if (String.IsNullOrWhiteSpace(description)) return false;
            if (String.IsNullOrWhiteSpace(_Settings.DocOnlyMarker)) return false;
            return description.Contains(_Settings.DocOnlyMarker, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<WorkflowProfile?> ResolveProfileAsync(Mission mission, CancellationToken token)
        {
            string? vesselId = mission.VesselId;
            if (String.IsNullOrWhiteSpace(vesselId)) return null;

            Vessel? vessel = !String.IsNullOrWhiteSpace(mission.TenantId)
                ? await _Database.Vessels.ReadAsync(mission.TenantId, vesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);

            if (vessel == null) return null;

            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = mission.TenantId,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0) return null;

            WorkflowProfile? match = ChooseBestFromScope(
                candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(p.VesselId, vesselId, StringComparison.Ordinal)).ToList());
            if (match != null) return match;

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestFromScope(
                    candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(p.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null) return match;
            }

            return ChooseBestFromScope(candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Global).ToList());
        }

        private WorkflowProfile? ChooseBestFromScope(List<WorkflowProfile> candidates)
        {
            if (candidates.Count == 0) return null;
            WorkflowProfile? defaultProfile = candidates.FirstOrDefault(p => p.IsDefault);
            return defaultProfile ?? candidates[0];
        }

        /// <summary>
        /// Build a profile's failing-test line pattern for the gate. A stored pattern that no longer builds
        /// (one written before save-time validation, or edited outside the service) is ignored with a named
        /// warning, so the built-in matchers still read the output.
        /// </summary>
        private Regex? ResolveFailedTestLinePattern(WorkflowProfile? profile, string subject)
        {
            if (String.IsNullOrWhiteSpace(profile?.FailedTestLinePattern)) return null;
            if (FailedTestLinePattern.TryCreate(profile!.FailedTestLinePattern, FailedTestLinePattern.MatchTimeout, out Regex? regex, out string? error))
                return regex;

            _Logging.Warn(_Header + subject + " workflow profile " + profile.Id + " FailedTestLinePattern ignored: " + error);
            return null;
        }

        /// <summary>
        /// A dock provisioned at a sibling pin is measured against that pin, so every command of the attempt receives
        /// the pin through <see cref="SiblingPinOverride.EnvironmentVariablePrefix"/> variables, and a profile that
        /// verifies its sibling checks that commit instead of the sibling's default branch. A pin that no longer
        /// resolves stops the gate.
        /// </summary>
        /// <returns>The refusal reason, or null when the gate may run.</returns>
        private async Task<string?> ApplySiblingPinsAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VoyageId) || String.IsNullOrWhiteSpace(mission.VesselId)) return null;
            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            List<ProvisionedSiblingPin> pins = await SiblingPinOverride.ResolvePinsAsync(_Database, vessel, mission.VoyageId,
                (repoPath, revision, ct) => _Git == null ? Task.FromResult<string?>(null) : _Git.GetRevisionCommitShaAsync(repoPath, revision, ct),
                token).ConfigureAwait(false);
            string? refusal = SiblingPinOverride.DescribeUnresolved(pins);
            if (refusal != null) return refusal;
            GateSlot? slot = _GateSlot.Value;
            if (slot != null) slot.SiblingPins = SiblingPinOverride.ToEnvironment(pins);
            return null;
        }

        /// <summary>
        /// The command environment with this attempt's sibling pins added. Armada sets the pins, so they win over a
        /// variable of the same name.
        /// </summary>
        private static IReadOnlyDictionary<string, string>? WithSiblingPins(IReadOnlyDictionary<string, string>? environment)
        {
            IReadOnlyDictionary<string, string>? pins = _GateSlot.Value?.SiblingPins;
            if (pins == null || pins.Count == 0) return environment;
            Dictionary<string, string> merged = environment == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(environment, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pin in pins) merged[pin.Key] = pin.Value;
            return merged;
        }

        private static Dictionary<string, string> MergeCommandEnvironment(
            WorkflowProfile? profile,
            IReadOnlyDictionary<string, string>? overrides)
        {
            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (profile?.EnvironmentVariables != null)
            {
                foreach (KeyValuePair<string, string> variable in profile.EnvironmentVariables)
                    if (!String.IsNullOrWhiteSpace(variable.Key)) environment[variable.Key] = variable.Value ?? String.Empty;
            }
            if (overrides != null)
            {
                foreach (KeyValuePair<string, string> variable in overrides)
                    environment[variable.Key] = variable.Value;
            }
            return environment;
        }

        private async Task<DefinitionOfDoneResult> RunCommandCoreAsync(
            string label,
            string command,
            string workingDir,
            CancellationToken token,
            Regex? failedTestLinePattern = null,
            IReadOnlyDictionary<string, string>? environment = null,
            WorkflowProfile? limitProfile = null)
        {
            environment = WithSiblingPins(environment);
            if (Armada.Core.Harbor.HarborCheckoutAddress.IsHarbor(workingDir))
                return await RunHarborCommandAsync(label, command, workingDir, token, failedTestLinePattern, environment, limitProfile).ConfigureAwait(false);
            ProcessStartInfo startInfo = new ProcessStartInfo(GetShell()) { WorkingDirectory = workingDir };
            BuildServerEnvironment.Apply(startInfo);
            foreach (KeyValuePair<string, string> variable in MergeCommandEnvironment(limitProfile, environment))
                startInfo.Environment[variable.Key] = variable.Value;
            if (OperatingSystem.IsWindows())
            {
                startInfo.Arguments = "/c " + command;
            }
            else
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
            }

            // The command owns its process group, so a timeout or cancellation also stops a background child it
            // started. Each stream keeps 16 MiB: the failing-test extraction below reads the whole runner output,
            // and a truncated output marks the extracted set as incomplete.
            // The profile the command comes from may set its own limit; every other command uses the global one.
            GateExecutionBudget? budget = ActiveExecutionBudget;
            TimeSpan commandTimeout = budget?.Limit ?? ResolveCommandTimeout(ResolveCommandTimeoutSeconds(limitProfile));
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, commandTimeout)
            {
                OutputLimitBytes = _CommandOutputLimitBytes,
                OwnProcessGroup = true
            };

            // More full build or test commands at once than the host can carry fail in ways that look like broken
            // code, so every command runs under the host-wide slot. An evaluation takes the slot at its first
            // command and keeps it until it ends: releasing between commands would send the gate to the back of
            // the queue for each of its build, test and consumer commands. A command outside an evaluation takes
            // the slot for that command alone.
            // The command's time limit starts when its process starts, after the slot is granted, so a wait for
            // the slot never counts against it. The wait is logged on its own line so the two are not confused.
            GateSlot? gateSlot = _GateSlot.Value;
            IDisposable? commandLease = null;
            Stopwatch slotWait = Stopwatch.StartNew();
            if (gateSlot != null)
            {
                await gateSlot.EnsureHeldAsync(token).ConfigureAwait(false);
            }
            else
            {
                commandLease = await HostWideCommandLock.AcquireAsync(null, "definition-of-done " + label, token).ConfigureAwait(false);
            }
            slotWait.Stop();

            // Whether another build or test command held a slot while this command ran is read before the lease can
            // be released, so a time limit can say whether the command had the host to itself.
            IDisposable? heldLease = gateSlot != null ? gateSlot.Lease : commandLease;
            long shareMark = HostWideCommandLock.ReadShareMark(heldLease);
            bool sharedHost = false;
            if (slotWait.Elapsed >= TimeSpan.FromSeconds(1))
            {
                _Logging.Info(_Header + label + " command waited "
                    + slotWait.Elapsed.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " s for the host command slot; its time limit starts now");
            }
            _Logging.Info(_Header + "running " + label + " command");

            BoundedProcessResult result;
            using (commandLease)
            {
                if (budget != null)
                {
                    if (!budget.TryGetRemaining(out commandTimeout)) return budget.Failure(label);
                    request.Timeout = commandTimeout;
                }
                Stopwatch commandElapsed = Stopwatch.StartNew();
                bool deadlineFired = false;
                try
                {
                    result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
                    deadlineFired = result.TimedOut;
                    sharedHost = HostWideCommandLock.SharedSince(heldLease, shareMark);
                }
                catch (Exception ex)
                {
                    ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, commandElapsed.Elapsed, ArmadaMetrics.OutcomeError);
                    string message = label + " command could not be started or completed: " + ex.Message;
                    _Logging.Warn(_Header + label + " command infrastructure failure");
                    return DefinitionOfDoneResult.Fail(
                        label,
                        -1,
                        BuildDiagnosticText(message),
                        DefinitionOfDoneFailureClassEnum.Infra);
                }
                finally
                {
                    budget?.RecordNativeExecution(commandElapsed.Elapsed, deadlineFired);
                }
            }
            if (budget?.HasFailure == true && !result.Cancelled)
                ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, result.Duration, ArmadaMetrics.OutcomeTimeout);
            else
                ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationDefinitionOfDone, result);

            if (result.KillError != null)
                _Logging.Warn(_Header + "could not kill command process: " + result.KillError);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }

            string combined = CombineOutput(result.StandardOutput, result.StandardError);
            if (result.TimedOut)
            {
                // Report the timeout that actually fired, not the configured one, so the message
                // stays true when the two differ.
                string message = label + " command timed out after "
                    + commandTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                    + " seconds.";
                // The limit counts everything the command does after it starts, including a lock it takes itself
                // (for example flock around a shared suite), so the processes still running at the limit are named.
                if (result.ProcessesAtTimeout.Count > 0)
                    message += " Still running at the limit: " + SecretRedactor.Redact(String.Join("; ", result.ProcessesAtTimeout));
                if (sharedHost)
                    message += " Another build or test command held a host command slot while this command ran.";
                bool progressing = WasWritingOutputAtLimit(result.OutputQuietAtTimeout, commandTimeout);
                if (progressing)
                    message += " The command was still writing output at the limit.";
                string timedOutOutput = String.IsNullOrWhiteSpace(combined) ? message : message + "\n" + combined;
                _Logging.Warn(_Header + message);

                // A run that named failing tests before its time limit failed on those tests: the class is
                // TestFail, with the time limit recorded as its own fact, so recovery reads the test failure
                // instead of treating the whole run as a host fault. A run that named none is a Timeout.
                FailedTestNameExtractor.FailedTestNameSet timedOutFailures = FailedTestNameExtractor.Extract(combined, failedTestLinePattern);
                bool namedFailures = timedOutFailures.Names.Count > 0;
                DefinitionOfDoneResult timeoutResult = DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(timedOutOutput, failedTestLinePattern),
                    namedFailures
                        ? DefinitionOfDoneFailureClassEnum.TestFail
                        : _FailureClassifier.Classify(label, -1, timedOutOutput, true));
                timeoutResult.TimedOut = true;
                timeoutResult.TimedOutOnSharedHost = sharedHost;
                timeoutResult.TimedOutWhileProgressing = progressing;
                if (namedFailures)
                {
                    // The tests after the time limit never ran, so the set is incomplete and is never compared.
                    timeoutResult.FailedTestNames = timedOutFailures.Names;
                    timeoutResult.FailedTestNamesOverflow = true;
                }
                AttachCommandOutput(timeoutResult, timedOutOutput, result);
                return timeoutResult;
            }

            string anomalies = BoundedProcessRunner.DescribeAnomalies(result);
            if (anomalies.Length > 0)
                _Logging.Warn(_Header + label + " command output: " + anomalies);

            int exitCode = result.ExitCode ?? -1;
            _Logging.Info(_Header + label + " command exited " + exitCode);

            if (exitCode == 0)
            {
                DefinitionOfDoneResult passed = DefinitionOfDoneResult.Pass();
                AttachCommandOutput(passed, label + " command output:\n" + combined, result);
                return passed;
            }

            DefinitionOfDoneFailureClassEnum failureClass = _FailureClassifier.Classify(
                label,
                exitCode,
                combined);
            string diagnosticText = BuildDiagnosticText(combined, failedTestLinePattern);
            // No test failed, so the reason leads with that and with the step that did fail; a reader of
            // the test digest alone would otherwise look for a failing test that does not exist.
            if (failureClass == DefinitionOfDoneFailureClassEnum.PostTestStep)
                diagnosticText = RedactAndBoundLine(DefinitionOfDoneFailureClassifier.DescribePostTestStepFailure(combined, exitCode)) + "\n" + diagnosticText;
            DefinitionOfDoneResult failResult = DefinitionOfDoneResult.Fail(
                label,
                exitCode,
                diagnosticText,
                failureClass);
            AttachCommandOutput(failResult, combined, result);

            // Extract the failing test identifiers here, where the runner output is still whole:
            // the diagnostic text keeps only a bounded, redacted tail, so a later reader could not
            // recover the complete set. Only a test failure carries a set; every other class leaves
            // it null. A rescue-vs-parent comparison reads this set to detect an unchanged failure,
            // and a set read from truncated output is marked incomplete so it is never compared.
            if (failureClass == DefinitionOfDoneFailureClassEnum.TestFail)
            {
                FailedTestNameExtractor.FailedTestNameSet failedTests = FailedTestNameExtractor.Extract(combined, failedTestLinePattern);
                if (failedTests.PatternTimedOut)
                {
                    _Logging.Warn(_Header + label + " failing test names unreadable: the profile's FailedTestLinePattern exceeded its "
                        + FailedTestLinePattern.MatchTimeout.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                        + " ms match timeout");
                }
                failResult.FailedTestNames = failedTests.Names;
                failResult.FailedTestNamesOverflow = failedTests.Overflow || result.Truncated;
            }

            return failResult;
        }

        private static void AttachCommandOutput(DefinitionOfDoneResult result, string output, BoundedProcessResult processResult)
        {
            result.GateCommandOutput = output;
            result.RunnerOutputTruncated = processResult.Truncated;
            result.RunnerOutputOmittedBytes = processResult.StandardOutputOmittedBytes + processResult.StandardErrorOmittedBytes;
            result.RunnerOutputIncomplete = processResult.TimedOut || processResult.Truncated
                || processResult.OutputDrainTimedOut || processResult.StillRunningAfterKill || processResult.KillError != null
                || processResult.EscapedProcessesKilled > 0 || processResult.ContainmentUnreadableProcesses > 0
                || processResult.ContainmentSweepError != null;
        }

        private static string CombineOutput(string? stdout, string? stderr)
        {
            string combined = stdout ?? String.Empty;
            if (!String.IsNullOrEmpty(stderr))
                combined += "\n--- STDERR ---\n" + stderr;
            return combined;
        }

        private string BuildDiagnosticText(string output)
        {
            return BuildDiagnosticText(output, null);
        }

        // The failing and skipped tests are read from the whole output before any bounding and lead
        // the text, because the bounded diagnostics and tail can drop a failing test that printed
        // early or on standard error. When the whole text is over its cap, the start of the
        // diagnostics gives way, so the digest and the end of the tail are always kept.
        private string BuildDiagnosticText(string output, Regex? failedTestLinePattern)
        {
            string body = BuildBoundedDiagnosticText(output);
            string? digest = TestOutcomeDigest.Render(output, failedTestLinePattern, _FullGateOutputPointer);
            if (digest == null) return body;
            string head = digest + "\n";
            int budget = _MAX_DIAGNOSTIC_TEXT_CHARS - head.Length;
            const string cutMarker = "...(diagnostics truncated)\n";
            if (body.Length > budget)
                body = cutMarker + body.Substring(body.Length - (budget - cutMarker.Length));
            return head + body;
        }

        private string BuildBoundedDiagnosticText(string output)
        {
            string[] lines = (output ?? String.Empty).Split('\n');
            HashSet<string> retained = new HashSet<string>(StringComparer.Ordinal);
            List<string> diagnosticLines = new List<string>();

            foreach (string line in lines)
            {
                if (diagnosticLines.Count >= _Settings.DiagnosticLines)
                    break;
                if (!DefinitionOfDoneFailureClassifier.IsActionableDiagnosticLine(line))
                    continue;

                string redacted = RedactAndBoundLine(line);
                if (!String.IsNullOrWhiteSpace(redacted) && retained.Add(redacted))
                    diagnosticLines.Add(redacted);
            }

            int tailCount = Math.Min(lines.Length, _Settings.OutputTailLines);
            int startIndex = lines.Length - tailCount;
            List<string> reversedTailLines = new List<string>();
            for (int i = lines.Length - 1; i >= startIndex; i--)
            {
                string redacted = RedactAndBoundLine(lines[i]);
                if (!String.IsNullOrWhiteSpace(redacted) && retained.Add(redacted))
                    reversedTailLines.Add(redacted);
            }
            reversedTailLines.Reverse();

            string diagnostics = diagnosticLines.Count == 0
                ? "(none recognized)"
                : BuildBoundedSection(diagnosticLines, false);
            string tail = reversedTailLines.Count == 0
                ? "(no additional output)"
                : BuildBoundedSection(reversedTailLines, true);

            string result = "--- ACTIONABLE DIAGNOSTICS ---\n" + diagnostics
                + "\n--- OUTPUT TAIL ---\n" + tail;
            if (result.Length > _MAX_DIAGNOSTIC_TEXT_CHARS)
                return result.Substring(0, _MAX_DIAGNOSTIC_TEXT_CHARS);
            return result;
        }

        private static string RedactAndBoundLine(string line)
        {
            string normalized = line.TrimEnd('\r');
            string redacted = _SecretLikePattern.Replace(normalized, match =>
            {
                int separatorIndex = match.Value.IndexOfAny(new char[] { '=', ':' });
                if (separatorIndex < 0) return match.Value;
                return match.Value.Substring(0, separatorIndex + 1) + " [REDACTED]";
            });

            if (redacted.Length <= _MAX_LINE_CHARS)
                return redacted;
            return redacted.Substring(0, _MAX_LINE_CHARS) + "...(line truncated)";
        }

        private static string BuildBoundedSection(IReadOnlyList<string> lines, bool keepEnd)
        {
            const string truncatedMarker = "...(section truncated)";
            int contentBudget = _MAX_SECTION_CHARS - truncatedMarker.Length - 1;
            List<string> selected = new List<string>();
            int usedCharacters = 0;

            if (keepEnd)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    int required = lines[i].Length + 1;
                    if (usedCharacters + required > contentBudget)
                        break;
                    selected.Add(lines[i]);
                    usedCharacters += required;
                }
                selected.Reverse();
                if (selected.Count < lines.Count)
                    selected.Insert(0, truncatedMarker);
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    int required = lines[i].Length + 1;
                    if (usedCharacters + required > contentBudget)
                        break;
                    selected.Add(lines[i]);
                    usedCharacters += required;
                }
                if (selected.Count < lines.Count)
                    selected.Add(truncatedMarker);
            }

            return String.Join("\n", selected);
        }

        /// <summary>
        /// Strips the <c>--no-restore</c> token from a shell command string so the build or
        /// test tool performs its own NuGet restore. Called only when
        /// <see cref="DefinitionOfDoneSettings.RunRestoreBeforeBuild"/> is true.
        /// A command that does not contain <c>--no-restore</c> is returned unchanged.
        /// </summary>
        private static string EnsureRestore(string command)
        {
            return Regex.Replace(command, @"(^|\s)--no-restore\b", " ", RegexOptions.IgnoreCase).Trim();
        }

        private string GetShell()
        {
            if (OperatingSystem.IsWindows()) return "cmd.exe";
            return "/bin/sh";
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// The host slot one gate evaluation holds from its first command until it ends.
        /// </summary>
        private sealed class GateSlot
        {
            private readonly string _MissionId;
            private readonly string? _VesselId;
            private readonly bool _Alone;
            private IDisposable? _Lease = null;

            public GateSlot(string missionId, string? vesselId, List<DefinitionOfDoneCommandPass> fullPasses, bool alone)
            {
                _MissionId = missionId;
                _VesselId = vesselId;
                OperationId = "dod:" + Guid.NewGuid().ToString("N");
                FullPasses = fullPasses;
                _Alone = alone;
            }

            public string MissionId => _MissionId;

            public string? VesselId => _VesselId;

            public string OperationId { get; }

            /// <summary>Sibling pin variables every command of this attempt receives.</summary>
            public IReadOnlyDictionary<string, string> SiblingPins { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Commands this attempt ran in full and passed on a clean worktree at a known HEAD.</summary>
            public List<DefinitionOfDoneCommandPass> FullPasses { get; }

            /// <summary>The host slot lease this attempt holds, or null before its first command.</summary>
            public IDisposable? Lease
            {
                get => _Lease;
            }

            public async Task EnsureHeldAsync(CancellationToken token)
            {
                if (_Lease != null) return;
                _Lease = _Alone
                    ? await HostWideCommandLock.AcquireAloneAsync(_MissionId, "definition-of-done gate re-run, alone on the host, for mission " + _MissionId, _VesselId, token).ConfigureAwait(false)
                    : await HostWideCommandLock.AcquireAsync(_MissionId, "definition-of-done gate for mission " + _MissionId, _VesselId, token).ConfigureAwait(false);
            }

            public void Release()
            {
                IDisposable? lease = _Lease;
                _Lease = null;
                lease?.Dispose();
            }
        }

        #endregion
    }
}
