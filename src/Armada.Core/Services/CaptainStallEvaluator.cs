namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// The one rule that decides whether a working captain is stalled.
    /// </summary>
    /// <remarks>
    /// Output alone is not evidence of a stall: some runtimes stream nothing between tool calls, so a
    /// captain can be quiet for a whole turn while it edits files and commits. A captain is stalled
    /// only when every signal is older than the stall window: its output (the heartbeat, or the
    /// provider-progress time for a runtime that reports it), the newest write in its dock worktree
    /// outside <c>.git</c>, and the committer time of its branch tip. Any one signal inside the window
    /// clears the stall. A build or test the agent started and is waiting on also clears it: a live
    /// descendant of the agent process that started after the agent's last output holds the window
    /// open until the child-process ceiling, measured from that output. The agent process itself and
    /// the companions it starts at launch never count, so an idle agent CLI is not progress. The
    /// autonomous recovery nudge and the admiral heartbeat-stall restart both call this evaluator,
    /// and both record the decision through <see cref="RecordAsync"/>.
    /// </remarks>
    public sealed class CaptainStallEvaluator
    {
        #region Public-Members

        /// <summary>Event recorded when work evidence clears a quiet captain.</summary>
        public const string ClearedEventType = "captain.stall_cleared";

        /// <summary>Event recorded when every signal confirms a stall.</summary>
        public const string ConfirmedEventType = "captain.stall_confirmed";

        /// <summary>
        /// Deciding signal: the captain is waiting for or holding the suite lease used around a full-suite run.
        /// </summary>
        public const string SignalSuiteLease = "suite_lease";

        /// <summary>Deciding signal: the captain's output is inside the window.</summary>
        public const string SignalOutput = "output";

        /// <summary>Deciding signal: the dock worktree was written inside the window.</summary>
        public const string SignalDockWrite = "dock_write";

        /// <summary>Deciding signal: the branch tip was committed inside the window.</summary>
        public const string SignalBranchTip = "branch_tip";

        /// <summary>
        /// Deciding signal: a descendant of the agent process that started after its last output is
        /// still running, and the child-process ceiling is not reached.
        /// </summary>
        public const string SignalChildProcess = "child_process";

        /// <summary>Deciding signal for a confirmed stall: no signal is inside the window.</summary>
        public const string SignalNone = "no_signal";

        /// <summary>Default bound on dock entries one evaluation reads.</summary>
        public const int DefaultMaxDockEntries = 50000;

        /// <summary>
        /// How long before the agent's last output a descendant may have started and still count. The
        /// agent usually announces a command just after it starts it.
        /// </summary>
        public static readonly TimeSpan ChildProcessStartSlack = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Descendants started this soon after the agent process are launch companions, such as the
        /// runtime binary under a wrapper or its tool servers, and never count as work.
        /// </summary>
        public static readonly TimeSpan LaunchCompanionWindow = TimeSpan.FromSeconds(60);

        #endregion

        #region Private-Members

        private const string _Header = "[CaptainStallEvaluator] ";

        private readonly DatabaseDriver _Database;
        private readonly IGitService? _Git;
        private readonly LoggingModule _Logging;
        private readonly int _MaxDockEntries;
        private readonly ICaptainProcessTreeReader _ProcessTree;

        // Last cleared decision recorded per decision path and mission, so a quiet but working captain
        // records one event per clearing signal per stall window instead of one per sweep tick.
        private readonly ConcurrentDictionary<string, RecordedClear> _RecordedClears =
            new ConcurrentDictionary<string, RecordedClear>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="git">Git service used to read the branch tip; null reports the tip as unavailable.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="maxDockEntries">Bound on dock entries one evaluation reads.</param>
        /// <param name="processTree">Reader for the agent's descendant processes; the host's <c>/proc</c> by default.</param>
        public CaptainStallEvaluator(DatabaseDriver database, IGitService? git, LoggingModule logging, int maxDockEntries = DefaultMaxDockEntries, ICaptainProcessTreeReader? processTree = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _MaxDockEntries = maxDockEntries > 0 ? maxDockEntries : DefaultMaxDockEntries;
            _ProcessTree = processTree ?? new ProcCaptainProcessTreeReader();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify the captain's output signal alone. <see cref="ProviderStallKind.None"/> means the
        /// output is inside the window and no stall decision is needed.
        /// </summary>
        /// <param name="captain">Captain.</param>
        /// <param name="lastProviderProgressUtc">Provider-progress time, when the runtime reports one.</param>
        /// <param name="windowMinutes">Stall window in minutes.</param>
        /// <param name="nowUtc">Reference time.</param>
        /// <returns>The output stall kind.</returns>
        public static ProviderStallKind ClassifyOutput(Captain captain, DateTime? lastProviderProgressUtc, double windowMinutes, DateTime nowUtc)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            return ProviderStallClassifier.Classify(captain.LastHeartbeatUtc, lastProviderProgressUtc, windowMinutes, nowUtc);
        }

        /// <summary>
        /// Decide whether the captain is stalled on its mission.
        /// </summary>
        /// <param name="captain">Working captain.</param>
        /// <param name="mission">Its current mission, when known.</param>
        /// <param name="windowMinutes">Stall window in minutes.</param>
        /// <param name="nowUtc">Reference time.</param>
        /// <param name="lastProviderProgressUtc">Provider-progress time, when the runtime reports one.</param>
        /// <param name="childProcessCeilingMinutes">
        /// Longest time, measured from the agent's last output, that a running descendant process holds
        /// the window open; 0 or less turns the child-process signal off.
        /// </param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The decision and the evidence behind it.</returns>
        public async Task<CaptainStallDecision> EvaluateAsync(
            Captain captain,
            Mission? mission,
            double windowMinutes,
            DateTime nowUtc,
            DateTime? lastProviderProgressUtc = null,
            double childProcessCeilingMinutes = 0,
            CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            double window = windowMinutes > 0 ? windowMinutes : 1.0;
            DateTime cutoffUtc = nowUtc.AddMinutes(-window);
            CaptainStallDecision decision = new CaptainStallDecision
            {
                WindowMinutes = window,
                NowUtc = nowUtc,
                LastHeartbeatUtc = captain.LastHeartbeatUtc,
                LastProviderProgressUtc = lastProviderProgressUtc,
                Kind = ClassifyOutput(captain, lastProviderProgressUtc, window, nowUtc)
            };

            if (decision.Kind == ProviderStallKind.None)
            {
                decision.DecidingSignal = SignalOutput;
                return decision;
            }

            Dock? dock = await ReadDockAsync(captain, mission, token).ConfigureAwait(false);
            decision.DockId = dock?.Id;
            if (dock == null)
            {
                decision.DockNote = "no dock recorded";
            }
            else if (String.IsNullOrWhiteSpace(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
            {
                decision.DockNote = "dock worktree is missing";
            }
            else
            {
                string worktree = dock.WorktreePath;
                DockWriteScan scan = await Task.Run(() => ScanNewestWrite(worktree, cutoffUtc, _MaxDockEntries), token).ConfigureAwait(false);
                decision.NewestDockWriteUtc = scan.NewestWriteUtc;
                decision.DockEntriesScanned = scan.EntriesScanned;
                decision.DockScanCapped = scan.Capped;
                decision.DockUnreadableDirectories = scan.UnreadableDirectories;
                if (scan.NewestWriteUtc.HasValue && scan.NewestWriteUtc.Value >= cutoffUtc)
                {
                    decision.DecidingSignal = SignalDockWrite;
                    return decision;
                }
            }

            decision.BranchName = !String.IsNullOrWhiteSpace(mission?.BranchName) ? mission!.BranchName : dock?.BranchName;
            decision.BranchTipUtc = await ReadBranchTipTimeAsync(mission, dock, decision.BranchName, token).ConfigureAwait(false);
            if (decision.BranchTipUtc.HasValue && decision.BranchTipUtc.Value >= cutoffUtc)
            {
                decision.DecidingSignal = SignalBranchTip;
                return decision;
            }

            if (ReadChildProcessSignal(captain, mission, decision, childProcessCeilingMinutes, nowUtc, lastProviderProgressUtc))
            {
                decision.DecidingSignal = SignalChildProcess;
                return decision;
            }

            if (CaptainSuiteLeaseRegistry.IsActive(mission?.Id))
            {
                decision.DecidingSignal = SignalSuiteLease;
                return decision;
            }

            decision.IsStalled = true;
            decision.DecidingSignal = SignalNone;
            return decision;
        }

        /// <summary>
        /// Record a stall decision as an event naming the signal that decided it and the evidence.
        /// Every confirmed stall is recorded. A cleared stall is recorded when it first clears or its
        /// clearing signal changes, then at most once per stall window while that signal keeps
        /// clearing it; every decision is logged.
        /// </summary>
        /// <param name="decision">Decision from <see cref="EvaluateAsync"/>.</param>
        /// <param name="decisionPath">Which path decided, for example the recovery nudge or the admiral restart.</param>
        /// <param name="captain">Captain.</param>
        /// <param name="mission">Mission, when known.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when an event was written.</returns>
        public async Task<bool> RecordAsync(CaptainStallDecision decision, string decisionPath, Captain captain, Mission? mission, CancellationToken token = default)
        {
            if (decision == null) throw new ArgumentNullException(nameof(decision));
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            string subject = "captain " + captain.Id + (mission != null ? " on mission " + mission.Id : String.Empty);
            string evidence = decision.DescribeEvidence();
            string key = decisionPath + "|" + (mission?.Id ?? captain.Id);

            if (!decision.IsStalled)
            {
                _Logging.Info(_Header + decisionPath + ": stall cleared by " + decision.DecidingSignal + " for " + subject + " -- " + evidence);
                if (_RecordedClears.TryGetValue(key, out RecordedClear? previous)
                    && previous != null
                    && String.Equals(previous.Signal, decision.DecidingSignal, StringComparison.Ordinal)
                    && decision.NowUtc - previous.RecordedUtc < TimeSpan.FromMinutes(decision.WindowMinutes))
                {
                    return false;
                }

                _RecordedClears[key] = new RecordedClear(decision.DecidingSignal, decision.NowUtc);
            }
            else
            {
                _Logging.Warn(_Header + decisionPath + ": stall confirmed for " + subject + " -- " + evidence);
                _RecordedClears.TryRemove(key, out _);
            }

            ArmadaEvent evt = new ArmadaEvent(
                decision.IsStalled ? ConfirmedEventType : ClearedEventType,
                (decision.IsStalled
                    ? "Stall confirmed for " + subject + " (" + decisionPath + ", stall kind " + decision.Kind + "): no signal inside the "
                    : "Stall cleared by " + decision.DecidingSignal + " for " + subject + " (" + decisionPath + ", output stall kind " + decision.Kind + "): inside the ")
                + decision.WindowMinutes.ToString("0.#") + "-minute window. " + evidence)
            {
                TenantId = captain.TenantId ?? mission?.TenantId,
                UserId = captain.UserId ?? mission?.UserId,
                EntityType = "captain",
                EntityId = captain.Id,
                CaptainId = captain.Id,
                MissionId = mission?.Id,
                VesselId = mission?.VesselId,
                VoyageId = mission?.VoyageId,
                Payload = JsonSerializer.Serialize(new
                {
                    decisionPath,
                    stalled = decision.IsStalled,
                    decidingSignal = decision.DecidingSignal,
                    outputStallKind = decision.Kind.ToString(),
                    windowMinutes = decision.WindowMinutes,
                    lastHeartbeatUtc = decision.LastHeartbeatUtc,
                    lastProviderProgressUtc = decision.LastProviderProgressUtc,
                    dockId = decision.DockId,
                    dockNote = decision.DockNote,
                    newestDockWriteUtc = decision.NewestDockWriteUtc,
                    dockEntriesScanned = decision.DockEntriesScanned,
                    dockScanCapped = decision.DockScanCapped,
                    dockUnreadableDirectories = decision.DockUnreadableDirectories,
                    branchName = decision.BranchName,
                    branchTipUtc = decision.BranchTipUtc,
                    childProcessCeilingMinutes = decision.ChildProcessCeilingMinutes,
                    childProcessCount = decision.ChildProcessCount,
                    childProcessNames = decision.ChildProcessNames,
                    childProcessCeilingReached = decision.ChildProcessCeilingReached,
                    childProcessNote = decision.ChildProcessNote
                })
            };

            try
            {
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record the " + evt.EventType + " event for " + subject + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Find the newest write under a dock worktree, excluding any <c>.git</c> entry. Directory
        /// modification times count, so a deleted or renamed file is a write. The scan stops at the
        /// first write inside the window, or at <paramref name="maxEntries"/>, and says so.
        /// </summary>
        /// <param name="worktreePath">Dock worktree root.</param>
        /// <param name="cutoffUtc">Start of the stall window.</param>
        /// <param name="maxEntries">Bound on entries read.</param>
        /// <returns>The newest write found and how the scan ended.</returns>
        public static DockWriteScan ScanNewestWrite(string worktreePath, DateTime cutoffUtc, int maxEntries)
        {
            DockWriteScan scan = new DockWriteScan();
            DirectoryInfo root = new DirectoryInfo(worktreePath);
            if (!root.Exists) return scan;

            EnumerationOptions options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            scan.Observe(root.LastWriteTimeUtc);
            Stack<DirectoryInfo> pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = directory.EnumerateFileSystemInfos("*", options);
                    foreach (FileSystemInfo entry in entries)
                    {
                        if (String.Equals(entry.Name, ".git", StringComparison.Ordinal)) continue;

                        scan.EntriesScanned++;
                        scan.Observe(entry.LastWriteTimeUtc);
                        if (scan.NewestWriteUtc.HasValue && scan.NewestWriteUtc.Value >= cutoffUtc) return scan;
                        if (scan.EntriesScanned >= maxEntries)
                        {
                            scan.Capped = true;
                            return scan;
                        }

                        if (entry is DirectoryInfo child) pending.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    scan.UnreadableDirectories++;
                }
            }

            return scan;
        }

        #endregion

        #region Private-Methods

        // True when a descendant the agent started after its last output is still running inside the
        // ceiling. Every other outcome writes its reason to the decision, so a stall confirmed without
        // this signal says why the signal was absent.
        private bool ReadChildProcessSignal(Captain captain, Mission? mission, CaptainStallDecision decision, double ceilingMinutes, DateTime nowUtc, DateTime? lastProviderProgressUtc)
        {
            if (ceilingMinutes <= 0)
            {
                decision.ChildProcessNote = "child-process signal is off";
                return false;
            }

            decision.ChildProcessCeilingMinutes = ceilingMinutes;
            int? processId = captain.ProcessId ?? mission?.ProcessId;
            if (!processId.HasValue || processId.Value <= 0)
            {
                decision.ChildProcessNote = "no agent process recorded";
                return false;
            }

            DateTime? referenceUtc = lastProviderProgressUtc ?? captain.LastHeartbeatUtc;
            if (!referenceUtc.HasValue)
            {
                decision.ChildProcessNote = "no output time to measure the ceiling from";
                return false;
            }

            CaptainProcessTreeRead tree;
            try
            {
                tree = _ProcessTree.ReadDescendants(processId.Value);
            }
            catch (Exception ex)
            {
                tree = CaptainProcessTreeRead.Unavailable(CaptainProcessTreeRead.ReadFailedReason, ex.Message);
            }

            if (!tree.Available)
            {
                decision.ChildProcessNote = "process tree unreadable (" + tree.UnavailableReason + ")";
                _Logging.Debug(_Header + "child-process signal unavailable for captain " + captain.Id + " process " + processId.Value
                    + ": " + tree.UnavailableReason + (tree.Detail != null ? " (" + tree.Detail + ")" : String.Empty)
                    + "; deciding on output, dock and branch signals only");
                return false;
            }

            DateTime earliestStartUtc = referenceUtc.Value - ChildProcessStartSlack;
            DateTime? companionsUntilUtc = tree.RootStartedUtc.HasValue ? tree.RootStartedUtc.Value + LaunchCompanionWindow : (DateTime?)null;
            List<string> names = new List<string>();
            foreach (CaptainChildProcess child in tree.Descendants)
            {
                if (child.StartedUtc < earliestStartUtc) continue;
                if (companionsUntilUtc.HasValue && child.StartedUtc <= companionsUntilUtc.Value) continue;
                names.Add(child.Name);
            }

            decision.ChildProcessCount = names.Count;
            if (names.Count == 0)
            {
                decision.ChildProcessNote = "no descendant started after the last output (" + tree.Descendants.Count + " older or launch-time descendants)";
                return false;
            }

            decision.ChildProcessNames = String.Join(", ", names.Count > 5 ? names.GetRange(0, 5) : names) + (names.Count > 5 ? ", ..." : String.Empty);
            if (nowUtc - referenceUtc.Value >= TimeSpan.FromMinutes(ceilingMinutes))
            {
                decision.ChildProcessCeilingReached = true;
                return false;
            }

            return true;
        }

        private async Task<Dock?> ReadDockAsync(Captain captain, Mission? mission, CancellationToken token)
        {
            string? dockId = !String.IsNullOrWhiteSpace(mission?.DockId) ? mission!.DockId : captain.CurrentDockId;
            if (String.IsNullOrWhiteSpace(dockId)) return null;
            return await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
        }

        private async Task<DateTime?> ReadBranchTipTimeAsync(Mission? mission, Dock? dock, string? branchName, CancellationToken token)
        {
            if (_Git == null || String.IsNullOrWhiteSpace(branchName)) return null;

            string? vesselId = !String.IsNullOrWhiteSpace(mission?.VesselId) ? mission!.VesselId : dock?.VesselId;
            if (String.IsNullOrWhiteSpace(vesselId)) return null;

            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null || String.IsNullOrWhiteSpace(vessel.LocalPath)) return null;

            return await _Git.GetCommitTimeUtcAsync(vessel.LocalPath, branchName, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Types

        private sealed class RecordedClear
        {
            public RecordedClear(string signal, DateTime recordedUtc)
            {
                Signal = signal;
                RecordedUtc = recordedUtc;
            }

            public string Signal { get; }

            public DateTime RecordedUtc { get; }
        }

        #endregion
    }

    /// <summary>
    /// A stall decision and the evidence behind it.
    /// </summary>
    public sealed class CaptainStallDecision
    {
        /// <summary>True when every signal is older than the window.</summary>
        public bool IsStalled { get; set; } = false;

        /// <summary>The signal that cleared the stall, or <see cref="CaptainStallEvaluator.SignalNone"/> when confirmed.</summary>
        public string DecidingSignal { get; set; } = CaptainStallEvaluator.SignalNone;

        /// <summary>How the output signal alone classifies.</summary>
        public ProviderStallKind Kind { get; set; } = ProviderStallKind.None;

        /// <summary>Stall window in minutes.</summary>
        public double WindowMinutes { get; set; } = 1.0;

        /// <summary>Reference time of the decision.</summary>
        public DateTime NowUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Captain heartbeat time.</summary>
        public DateTime? LastHeartbeatUtc { get; set; } = null;

        /// <summary>Provider-progress time, when reported.</summary>
        public DateTime? LastProviderProgressUtc { get; set; } = null;

        /// <summary>Dock the scan read, when one is recorded.</summary>
        public string? DockId { get; set; } = null;

        /// <summary>Why the dock could not be scanned, when it could not.</summary>
        public string? DockNote { get; set; } = null;

        /// <summary>Newest write found in the dock worktree outside .git.</summary>
        public DateTime? NewestDockWriteUtc { get; set; } = null;

        /// <summary>Dock entries read.</summary>
        public int DockEntriesScanned { get; set; } = 0;

        /// <summary>True when the scan stopped at its entry bound.</summary>
        public bool DockScanCapped { get; set; } = false;

        /// <summary>Directories the scan could not read.</summary>
        public int DockUnreadableDirectories { get; set; } = 0;

        /// <summary>Branch whose tip was read.</summary>
        public string? BranchName { get; set; } = null;

        /// <summary>Committer time of the branch tip, when it could be read.</summary>
        public DateTime? BranchTipUtc { get; set; } = null;

        /// <summary>Child-process ceiling in minutes the decision used; 0 when the signal was off.</summary>
        public double ChildProcessCeilingMinutes { get; set; } = 0;

        /// <summary>Running descendants that started after the agent's last output.</summary>
        public int ChildProcessCount { get; set; } = 0;

        /// <summary>Names of the counted descendants, at most five.</summary>
        public string? ChildProcessNames { get; set; } = null;

        /// <summary>True when counted descendants were running but the ceiling was reached.</summary>
        public bool ChildProcessCeilingReached { get; set; } = false;

        /// <summary>Why no descendant counted, when none did.</summary>
        public string? ChildProcessNote { get; set; } = null;

        /// <summary>Human-readable evidence for events and logs.</summary>
        public string DescribeEvidence()
        {
            List<string> parts = new List<string>();
            parts.Add("heartbeat " + Age(LastHeartbeatUtc));
            if (LastProviderProgressUtc.HasValue) parts.Add("provider progress " + Age(LastProviderProgressUtc));

            if (DockNote != null)
            {
                parts.Add("dock: " + DockNote);
            }
            else if (DockId != null || NewestDockWriteUtc.HasValue || DockEntriesScanned > 0)
            {
                string dock = "newest dock write " + Age(NewestDockWriteUtc) + " (" + DockEntriesScanned + " entries read outside .git";
                if (DockScanCapped) dock += ", scan stopped at its entry bound";
                if (DockUnreadableDirectories > 0) dock += ", " + DockUnreadableDirectories + " unreadable directories";
                parts.Add(dock + ")");
            }

            if (IsStalled || String.Equals(DecidingSignal, CaptainStallEvaluator.SignalBranchTip, StringComparison.Ordinal))
            {
                parts.Add(BranchTipUtc.HasValue
                    ? "branch tip " + (BranchName ?? "?") + " committed " + Age(BranchTipUtc)
                    : "branch tip unavailable" + (BranchName != null ? " for " + BranchName : " (no branch recorded)"));
            }

            if (ChildProcessCount > 0)
            {
                parts.Add(ChildProcessCount + " running child process(es) started after the last output (" + ChildProcessNames + ")"
                    + (ChildProcessCeilingReached ? ", but the " + ChildProcessCeilingMinutes.ToString("0.#") + "-minute child-process ceiling is reached" : String.Empty));
            }
            else if (ChildProcessNote != null && (IsStalled || ChildProcessCeilingMinutes > 0))
            {
                parts.Add("child processes: " + ChildProcessNote);
            }

            return String.Join("; ", parts) + ".";
        }

        private string Age(DateTime? utc)
        {
            if (!utc.HasValue) return "never recorded";
            return (NowUtc - utc.Value).TotalMinutes.ToString("0.0") + " min ago";
        }
    }

    /// <summary>
    /// Result of a dock worktree write scan.
    /// </summary>
    public sealed class DockWriteScan
    {
        /// <summary>Newest write time seen.</summary>
        public DateTime? NewestWriteUtc { get; set; } = null;

        /// <summary>Entries read.</summary>
        public int EntriesScanned { get; set; } = 0;

        /// <summary>True when the scan stopped at its entry bound.</summary>
        public bool Capped { get; set; } = false;

        /// <summary>Directories that could not be read.</summary>
        public int UnreadableDirectories { get; set; } = 0;

        /// <summary>Record one observed write time.</summary>
        public void Observe(DateTime writeUtc)
        {
            if (!NewestWriteUtc.HasValue || writeUtc > NewestWriteUtc.Value) NewestWriteUtc = writeUtc;
        }
    }
}
