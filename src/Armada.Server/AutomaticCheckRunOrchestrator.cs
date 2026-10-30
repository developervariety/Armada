namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Background resolver for pending check gates that become eligible as work lands.
    /// </summary>
    public sealed class AutomaticCheckRunOrchestrator
    {
        /// <summary>
        /// Event written once per dispatch-hold engagement when the sweep leaves pending checks waiting.
        /// </summary>
        public const string DeferredByDispatchHoldEvent = "check.auto_deferred_dispatch_hold";

        /// <summary>
        /// Event written when the host command slot starts holding its next request for free memory.
        /// </summary>
        public const string MemoryWaitStartedEvent = "check.memory_wait_started";

        /// <summary>
        /// Event written at each report interval while the host command slot stays held for free memory.
        /// </summary>
        public const string MemoryWaitContinuingEvent = "check.memory_wait_continuing";

        /// <summary>
        /// Event written when the host command slot stops holding its next request for free memory.
        /// </summary>
        public const string MemoryWaitEndedEvent = "check.memory_wait_ended";

        /// <summary>
        /// Event written when every host command slot is held while requests wait for one.
        /// </summary>
        public const string SlotBusyStartedEvent = "check.slot_busy_started";

        /// <summary>
        /// Event written at each report interval while every host command slot stays held with requests waiting.
        /// </summary>
        public const string SlotBusyContinuingEvent = "check.slot_busy_continuing";

        /// <summary>
        /// Event written when a host command slot frees or no request waits any longer.
        /// </summary>
        public const string SlotBusyEndedEvent = "check.slot_busy_ended";

        /// <summary>
        /// Event written when every place of this runner holds a started check while more checks are pending.
        /// </summary>
        public const string RunnerFullStartedEvent = "check.runner_full_started";

        /// <summary>
        /// Event written at each report interval while this runner stays full with checks pending.
        /// </summary>
        public const string RunnerFullContinuingEvent = "check.runner_full_continuing";

        /// <summary>
        /// Event written when this runner has a free place again or no check waits for one.
        /// </summary>
        public const string RunnerFullEndedEvent = "check.runner_full_ended";

        /// <summary>
        /// Event written when an automatic Build or UnitTest check starts waiting for a running
        /// definition-of-done gate of its voyage or mission on the same vessel.
        /// </summary>
        public const string GateOverlapStartedEvent = "check.gate_overlap_started";

        /// <summary>
        /// Event written at each report interval while checks still wait for such a gate.
        /// </summary>
        public const string GateOverlapContinuingEvent = "check.gate_overlap_continuing";

        /// <summary>
        /// Event written when no check waits for such a gate any longer.
        /// </summary>
        public const string GateOverlapEndedEvent = "check.gate_overlap_ended";

        /// <summary>
        /// Most checks this orchestrator keeps in flight at once. A check in flight has been stamped
        /// and handed to the executor; most of them wait in the host-wide command slot, which still
        /// runs one expensive command at a time. The bound limits how many isolated checkouts can
        /// sit on disk waiting for that slot.
        /// </summary>
        public const int MaxConcurrentChecks = 4;

        private readonly string _Header = "[AutomaticCheckRunOrchestrator] ";
        private readonly DatabaseDriver _Database;
        private readonly CheckRunService _CheckRuns;
        private readonly ReleaseService _Releases;
        private readonly IncidentService _Incidents;
        private readonly LoggingModule _Logging;
        private readonly StaleCheckSupersessionService _Supersession;
        private readonly DispatchHold? _DispatchHold;
        private readonly Func<DefinitionOfDoneSettings>? _GateSettings;
        private readonly Func<DateTime> _Clock;
        private DateTime? _ReportedHoldSetByUtc = null;

        private readonly WaitReport _MemoryWait = new WaitReport(
            "hold", MemoryWaitStartedEvent, MemoryWaitContinuingEvent, MemoryWaitEndedEvent,
            "Host command slot no longer held for free memory");
        private readonly WaitReport _SlotBusyWait = new WaitReport(
            "wait", SlotBusyStartedEvent, SlotBusyContinuingEvent, SlotBusyEndedEvent,
            "A host command slot is free or no request waits");
        private readonly WaitReport _GateOverlapWait = new WaitReport(
            "wait", GateOverlapStartedEvent, GateOverlapContinuingEvent, GateOverlapEndedEvent,
            "No automatic check waits for a running definition-of-done gate");

        // Checks the latest eligibility scan held back behind a running gate, keyed by check run id, with the
        // gate's mission id. Replaced on every scan.
        private readonly ConcurrentDictionary<string, string> _GateWaits = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private readonly WaitReport _RunnerFullWait = new WaitReport(
            "wait", RunnerFullStartedEvent, RunnerFullContinuingEvent, RunnerFullEndedEvent,
            "The automatic check runner has a free place or no check waits for one");

        // Guards discovery only (supersession, eligibility, start). Execution runs outside it, so
        // a long check never stops the next sweep from finding and starting a newly armed one.
        private readonly SemaphoreSlim _SweepGate = new SemaphoreSlim(1, 1);

        // Checks started by this orchestrator and not yet finished, keyed by check run id, so a sweep
        // never starts a record that an earlier sweep is still executing or queueing.
        private readonly ConcurrentDictionary<string, Task> _InFlight = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        private readonly JsonSerializerOptions _JsonOptions = JsonDefaults.Web;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="checkRuns">Check run service.</param>
        /// <param name="releases">Release service.</param>
        /// <param name="incidents">Incident service.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="dispatchHold">Fleet-wide dispatch hold, or null.</param>
        /// <param name="gateSettings">Reads the current gate settings for the memory-wait report intervals, or null for the defaults.</param>
        /// <param name="clock">UTC clock, or null for the system clock.</param>
        public AutomaticCheckRunOrchestrator(
            DatabaseDriver database,
            CheckRunService checkRuns,
            ReleaseService releases,
            IncidentService incidents,
            LoggingModule logging,
            DispatchHold? dispatchHold = null,
            Func<DefinitionOfDoneSettings>? gateSettings = null,
            Func<DateTime>? clock = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _CheckRuns = checkRuns ?? throw new ArgumentNullException(nameof(checkRuns));
            _Releases = releases ?? throw new ArgumentNullException(nameof(releases));
            _Incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Supersession = new StaleCheckSupersessionService(_Database, _Logging);
            _DispatchHold = dispatchHold;
            _GateSettings = gateSettings;
            _Clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Start a non-blocking sweep unless another sweep is still choosing checks. The sweep starts
        /// each eligible check and returns without waiting for it, so a check that runs for minutes
        /// does not keep the next sweep from starting a check armed in the meantime.
        /// </summary>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            if (!_SweepGate.Wait(0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await StartEligibleChecksAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "sweep failed: " + ex.Message);
                }
                finally
                {
                    _SweepGate.Release();
                }
            }, token);
        }

        /// <summary>
        /// Execute one sweep of eligible pending checks and wait for the checks it started.
        /// </summary>
        /// <returns>The number of checks this sweep started and finished.</returns>
        public async Task<int> RunSweepAsync(CancellationToken token = default)
        {
            List<Task> started;
            await _SweepGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                started = await StartEligibleChecksAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _SweepGate.Release();
            }

            await Task.WhenAll(started).ConfigureAwait(false);
            return started.Count;
        }

        /// <summary>
        /// Supersede stale records, then start every eligible pending check up to the in-flight bound.
        /// Each started check runs on its own task; the returned tasks finish when their checks do.
        /// </summary>
        private async Task<List<Task>> StartEligibleChecksAsync(CancellationToken token)
        {
            AuthContext auth = BuildSystemAuth();
            List<Task> started = new List<Task>();

            // A green that measured an earlier commit must be replaced before eligible work is
            // chosen, so the Pending record it leaves behind is picked up in this same sweep. Left
            // in place, that green would satisfy the Judge gate for work it never measured.
            try
            {
                int superseded = await _Supersession.SupersedeAsync(token).ConfigureAwait(false);
                if (superseded > 0)
                    _Logging.Info(_Header + "superseded " + superseded + " stale green check(s) whose work moved on");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "stale-check supersession failed: " + ex.Message);
            }

            // The dispatch hold stops checks that are unrelated to draining work already admitted into
            // a live voyage. Such a voyage may need its armed checks to finish before it can complete;
            // running missions are allowed to finish during the hold and their checks must drain too.
            // Checks for idle vessels, completed voyages, and releases remain deferred. An operator's
            // own check run is not affected.
            DispatchHoldSnapshot? hold = _DispatchHold?.Snapshot();
            if (hold != null)
            {
                await ReportHoldDeferralAsync(hold, token).ConfigureAwait(false);
            }
            else
            {
                _ReportedHoldSetByUtc = null;
            }
            HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
            await ReportWaitsAsync(slot, token).ConfigureAwait(false);

            int capacity = MaxConcurrentChecks - _InFlight.Count;
            if (capacity <= 0)
            {
                _Logging.Debug(_Header + "no check started: " + DescribeInFlight(slot));
                return started;
            }

            _GateWaits.Clear();
            List<CheckRun> eligible = await FindEligiblePendingChecksAsync(capacity, hold, token).ConfigureAwait(false);
            await ReportGateOverlapAsync(slot, token).ConfigureAwait(false);
            if (eligible.Count == 0) return started;

            _Logging.Info(_Header + "starting " + eligible.Count + " eligible pending check(s); "
                + DescribeInFlight(HostWideCommandLock.Snapshot()));
            foreach (CheckRun pending in eligible)
            {
                Task? execution = StartExecution(auth, pending, token);
                if (execution != null) started.Add(execution);
            }

            return started;
        }

        /// <summary>
        /// Say how many started checks run and how many wait for the host-wide slot, what holds it, and
        /// whether free memory holds the next request. A check waiting for the slot has no process yet, so
        /// counting it as running reads like a stall.
        /// </summary>
        /// <param name="slot">A snapshot of the host-wide slot.</param>
        /// <returns>A one-line description.</returns>
        internal string DescribeInFlight(HostSlotSnapshot slot)
        {
            int waiting = 0;
            foreach (string id in _InFlight.Keys)
            {
                if (slot.FindWaiter(id) != null) waiting++;
            }

            int running = _InFlight.Count - waiting;
            string text = running + " check(s) running, " + waiting + " waiting for the host build slot";
            if (slot.Holders.Count > 0)
            {
                List<string> holders = new List<string>();
                foreach (HostSlotHolder holder in slot.Holders)
                {
                    holders.Add(holder.Description + " for " + HeldSeconds(holder.SinceUtc) + " s");
                }

                text += "; slot held by " + String.Join(", ", holders);
                if (slot.SlotCount > 1) text += " (" + slot.Holders.Count + " of " + slot.SlotCount + " slots in use)";
            }
            else if (!String.IsNullOrEmpty(slot.HolderDescription))
            {
                text += "; slot held by " + slot.HolderDescription + " for " + HeldSeconds(slot.HolderSinceUtc) + " s";
            }

            if (slot.Waiters.Count > waiting) text += "; " + (slot.Waiters.Count - waiting) + " other request(s) queued for the slot";
            if (slot.HeldForMemory && !String.IsNullOrEmpty(slot.MemoryWaitReason)) text += "; next request " + slot.MemoryWaitReason;
            return text;
        }

        /// <summary>
        /// Report every wait that keeps a started or pending check from running: the host command slot
        /// held for free memory, every host command slot busy while requests wait, and every place of this
        /// runner taken while more checks are pending. Each wait records an event when it starts, one at
        /// each report interval while it lasts and one when it ends. A waiting check has no process, so without this a queue of checks reads
        /// like an idle one.
        /// </summary>
        /// <param name="slot">A snapshot of the host-wide slot.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task ReportWaitsAsync(HostSlotSnapshot slot, CancellationToken token)
        {
            DateTime now = _Clock();
            DefinitionOfDoneSettings settings = _GateSettings?.Invoke() ?? new DefinitionOfDoneSettings();
            AutomaticCheckRunPlaces.Publish(_InFlight.Count, MaxConcurrentChecks);

            bool memoryHeld = slot.HeldForMemory && slot.MemoryHeldSinceUtc.HasValue;
            await StepWaitAsync(_MemoryWait, memoryHeld, slot.MemoryHeldSinceUtc?.ToString("o"), slot.MemoryHeldSinceUtc ?? now,
                since => Task.FromResult(DescribeMemoryWait(slot, since, now)), slot, settings, now, token).ConfigureAwait(false);

            bool slotBusy = !slot.HeldForMemory && slot.Waiters.Count > 0 && slot.Holders.Count >= Math.Max(1, slot.SlotCount);
            await StepWaitAsync(_SlotBusyWait, slotBusy, "busy", now,
                since => Task.FromResult(DescribeSlotBusy(slot, since, now)), slot, settings, now, token).ConfigureAwait(false);

            int waitingForPlace = _InFlight.Count >= MaxConcurrentChecks
                ? await CountPendingOutsideRunnerAsync(token).ConfigureAwait(false)
                : 0;
            await StepWaitAsync(_RunnerFullWait, waitingForPlace > 0, "full", now,
                since => Task.FromResult(DescribeRunnerFull(slot, waitingForPlace, since, now)), slot, settings, now, token).ConfigureAwait(false);
        }

        private async Task StepWaitAsync(
            WaitReport report,
            bool active,
            string? identity,
            DateTime sinceWhenNew,
            Func<DateTime, Task<string>> describe,
            HostSlotSnapshot slot,
            DefinitionOfDoneSettings settings,
            DateTime now,
            CancellationToken token)
        {
            if (report.SinceUtc.HasValue && (!active || !String.Equals(identity, report.Identity, StringComparison.Ordinal)))
            {
                DateTime endedSince = report.SinceUtc.Value;
                report.SinceUtc = null;
                report.Identity = null;
                string ended = report.EndedText + "; the " + report.Noun + " lasted " + WaitMinutes(endedSince, now)
                    + " min since " + endedSince.ToString("u") + ".";
                _Logging.Info(_Header + ended);
                await WriteWaitEventAsync(report.EndedEvent, ended, report, slot, endedSince, token).ConfigureAwait(false);
            }

            if (!active) return;

            string description;
            if (!report.SinceUtc.HasValue)
            {
                report.SinceUtc = sinceWhenNew;
                report.Identity = identity;
                report.ReportedUtc = now;
                description = await describe(report.SinceUtc.Value).ConfigureAwait(false);
                _Logging.Info(_Header + description);
                await WriteWaitEventAsync(report.StartedEvent, description, report, slot, report.SinceUtc.Value, token).ConfigureAwait(false);
            }
            else if (now - report.ReportedUtc >= TimeSpan.FromMinutes(settings.HostSlotWaitReportMinutes))
            {
                report.ReportedUtc = now;
                description = await describe(report.SinceUtc.Value).ConfigureAwait(false);
                _Logging.Info(_Header + description);
                await WriteWaitEventAsync(report.ContinuingEvent, description, report, slot, report.SinceUtc.Value, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Count the pending checks this runner has not started, which wait for one of its places and have
        /// not asked for the host command slot. Counts at most one page.
        /// </summary>
        private async Task<int> CountPendingOutsideRunnerAsync(CancellationToken token)
        {
            try
            {
                EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    Status = CheckRunStatusEnum.Pending,
                    Source = CheckRunSourceEnum.Armada,
                    PageNumber = 1,
                    PageSize = 200
                }, token).ConfigureAwait(false);
                return page.Objects.Count(run => !_InFlight.ContainsKey(run.Id));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not count pending checks waiting for a runner place: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Name the memory reading and every request waiting for the host command slot.
        /// </summary>
        private static string DescribeMemoryWait(HostSlotSnapshot slot, DateTime since, DateTime now)
        {
            return "Host command slot held for free memory for " + WaitMinutes(since, now) + " min since "
                + since.ToString("u") + ": " + (slot.MemoryWaitReason ?? ("floor " + slot.MemoryFloorMb + " MB")) + ". "
                + DescribeWaiters(slot);
        }

        /// <summary>
        /// Name every holder of the host command slots, since when each holds its slot, and every waiter in order.
        /// </summary>
        private static string DescribeSlotBusy(HostSlotSnapshot slot, DateTime since, DateTime now)
        {
            List<string> holders = new List<string>();
            foreach (HostSlotHolder holder in slot.Holders)
            {
                holders.Add((holder.Description ?? holder.Key ?? "unnamed holder") + (holder.SinceUtc.HasValue ? " since " + holder.SinceUtc.Value.ToString("u") : ""));
            }

            return "Every host command slot busy with requests waiting for " + WaitMinutes(since, now) + " min since "
                + since.ToString("u") + ": " + Math.Max(1, slot.SlotCount) + " slot(s) held by " + String.Join("; ", holders) + ". "
                + DescribeWaiters(slot);
        }

        /// <summary>
        /// Say that every place of this runner holds a started check while more checks are pending.
        /// </summary>
        private string DescribeRunnerFull(HostSlotSnapshot slot, int waitingForPlace, DateTime since, DateTime now)
        {
            int waitingForSlot = 0;
            foreach (string id in _InFlight.Keys)
            {
                if (slot.FindWaiter(id) != null) waitingForSlot++;
            }

            return "Automatic check runner full for " + WaitMinutes(since, now) + " min since " + since.ToString("u") + ": "
                + _InFlight.Count + " of " + MaxConcurrentChecks + " places hold started checks (" + waitingForSlot
                + " of them wait for the host command slot); " + waitingForPlace
                + " more pending check(s) wait for a place and have not asked for the slot.";
        }

        private static string DescribeWaiters(HostSlotSnapshot slot)
        {
            if (slot.Waiters.Count == 0) return "No request is waiting.";

            List<string> waiting = new List<string>();
            foreach (HostSlotWaiter waiter in slot.Waiters.OrderBy(waiter => waiter.Position))
            {
                waiting.Add(waiter.Position + ". " + (String.IsNullOrWhiteSpace(waiter.Description) ? (waiter.Key ?? "unnamed request") : waiter.Description));
            }

            return slot.Waiters.Count + " request(s) waiting: " + String.Join("; ", waiting) + ".";
        }

        private async Task WriteWaitEventAsync(string eventType, string message, WaitReport report, HostSlotSnapshot slot, DateTime since, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = Constants.DefaultTenantId,
                Payload = JsonSerializer.Serialize(new
                {
                    Wait = report.Noun,
                    SinceUtc = since,
                    slot.SlotCount,
                    slot.AvailableMemoryMb,
                    slot.MemoryFloorMb,
                    HolderKeys = slot.Holders.Select(holder => holder.Key).ToList(),
                    WaitingKeys = slot.Waiters.OrderBy(waiter => waiter.Position).Select(waiter => waiter.Key).ToList(),
                    InFlight = _InFlight.Count,
                    MaxConcurrentChecks
                }, _JsonOptions)
            };

            try
            {
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record " + eventType + ": " + ex.Message);
            }
        }

        private static int WaitMinutes(DateTime since, DateTime now)
        {
            return Math.Max(0, (int)Math.Floor((now - since).TotalMinutes));
        }

        /// <summary>
        /// One kind of wait being reported: its event names, and while it lasts, when it started and when it
        /// was last reported. Written only inside the sweep gate.
        /// </summary>
        private sealed class WaitReport
        {
            public WaitReport(string noun, string startedEvent, string continuingEvent, string endedEvent, string endedText)
            {
                Noun = noun;
                StartedEvent = startedEvent;
                ContinuingEvent = continuingEvent;
                EndedEvent = endedEvent;
                EndedText = endedText;
            }

            public string Noun { get; }
            public string StartedEvent { get; }
            public string ContinuingEvent { get; }
            public string EndedEvent { get; }
            public string EndedText { get; }
            public DateTime? SinceUtc { get; set; } = null;
            public string? Identity { get; set; } = null;
            public DateTime ReportedUtc { get; set; } = DateTime.MinValue;
        }

        /// <summary>
        /// Run one check on its own task and record it as in flight until it finishes.
        /// </summary>
        /// <returns>The task that finishes with the check, or null when the check is already in flight.</returns>
        private Task? StartExecution(AuthContext auth, CheckRun pending, CancellationToken token)
        {
            TaskCompletionSource<bool> finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_InFlight.TryAdd(pending.Id, finished.Task)) return null;
            AutomaticCheckRunPlaces.Publish(_InFlight.Count, MaxConcurrentChecks);

            _ = Task.Run(async () =>
            {
                try
                {
                    CheckRun result = await ExecutePendingAsync(auth, pending, token).ConfigureAwait(false);

                    await RefreshLinkedReleasesAsync(auth, result, token).ConfigureAwait(false);
                    if (result.Status == CheckRunStatusEnum.Failed)
                        await CreateFailureIncidentAsync(result, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _Logging.Info(_Header + "check " + pending.Id + " stopped by shutdown before a verdict");
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "check " + pending.Id + " execution failed: " + ex.Message);
                }
                finally
                {
                    _InFlight.TryRemove(pending.Id, out Task? _);
                    AutomaticCheckRunPlaces.Publish(_InFlight.Count, MaxConcurrentChecks);
                    finished.TrySetResult(true);
                }
            }, CancellationToken.None);

            return finished.Task;
        }

        /// <summary>
        /// Name the hold once per engagement, so a quiet check queue does not read as an idle one.
        /// </summary>
        private async Task ReportHoldDeferralAsync(DispatchHoldSnapshot hold, CancellationToken token)
        {
            if (_ReportedHoldSetByUtc == hold.SetByUtc) return;

            string holder = String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy!;
            string message = "Automatic checks for idle vessels and non-live-voyage work deferred: dispatch_hold engaged by " + holder + " at "
                + hold.SetByUtc.ToString("u") + ": " + hold.Reason
                + " Eligible checks for already active voyages can run so their work can drain; other checks run after the hold clears.";
            _Logging.Info(_Header + message);

            ArmadaEvent evt = new ArmadaEvent(DeferredByDispatchHoldEvent, message)
            {
                TenantId = Constants.DefaultTenantId,
                Payload = JsonSerializer.Serialize(new { hold.SetBy, hold.SetByUtc, hold.Reason }, _JsonOptions)
            };
            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            _ReportedHoldSetByUtc = hold.SetByUtc;
        }

        private async Task<List<CheckRun>> FindEligiblePendingChecksAsync(int limit, DispatchHoldSnapshot? hold, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                Status = CheckRunStatusEnum.Pending,
                Source = CheckRunSourceEnum.Armada,
                ToUtc = DateTime.UtcNow,
                PageNumber = 1,
                PageSize = 200
            };
            List<CheckRun> eligible = new List<CheckRun>();
            int scanned = 0;

            try
            {
                while (eligible.Count < limit)
                {
                    EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
                    foreach (CheckRun run in page.Objects
                        .OrderBy(run => run.CreatedUtc)
                        .ThenBy(run => run.Id, StringComparer.Ordinal))
                    {
                        scanned++;
                        if (_InFlight.ContainsKey(run.Id)) continue;
                        if (hold != null && !await IsEligibleDuringDispatchHoldAsync(run, token).ConfigureAwait(false)) continue;
                        if (await IsEligibleAsync(run, token).ConfigureAwait(false))
                            eligible.Add(run);
                        if (eligible.Count >= limit) break;
                    }

                    if (page.Objects.Count < query.PageSize || query.PageNumber >= page.TotalPages)
                        break;
                    query.PageNumber++;
                }
            }
            catch (OperationCanceledException)
            {
                _Logging.Warn(_Header + "pending-check search incomplete: canceled after scanning "
                    + scanned + " record(s) through page " + query.PageNumber);
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "pending-check search incomplete after scanning "
                    + scanned + " record(s) through page " + query.PageNumber + ": " + ex.Message);
                throw;
            }

            return eligible;
        }

        /// <summary>
        /// A dispatch hold still permits checks needed to drain work in an existing live voyage.
        /// </summary>
        /// <remarks>
        /// The hold blocks new mission and voyage dispatches, not missions that were already running.
        /// A running mission can therefore produce work while the hold is active. Its voyage-armed
        /// check must run when that work becomes final, or the check itself prevents the voyage
        /// from completing and the deployment drain cannot finish. A timestamp cutoff would strand
        /// exactly that in-flight work. Other automatic checks remain deferred.
        /// </remarks>
        private async Task<bool> IsEligibleDuringDispatchHoldAsync(CheckRun run, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(run.VoyageId) || !String.IsNullOrWhiteSpace(run.DeploymentId)) return false;

            Voyage? voyage = await _Database.Voyages.ReadAsync(run.VoyageId, token).ConfigureAwait(false);
            return voyage != null && ObjectiveService.IsActiveVoyageStatus(voyage.Status);
        }

        /// <summary>
        /// The mission carrying the work a voyage-armed Check should measure: a stage that has
        /// committed to a branch, in the record's own lane when the voyage has split. Returns null
        /// while no stage has produced measurable work for the record.
        /// </summary>
        private async Task<Mission?> ResolveWorkUnderReviewAsync(CheckRun run, CancellationToken token)
        {
            List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(run.VoyageId!, token).ConfigureAwait(false);

            // One definition, shared with the supersession service and the Judge gate: the executor
            // stamps from it and the supersession compares against it, so they cannot disagree about
            // which commit a record ought to carry.
            return StaleCheckSupersessionService.SelectWorkUnderReviewFor(run, missions);
        }

        /// <summary>
        /// Point an armed Check at the work it is meant to measure before it executes.
        /// </summary>
        /// <remarks>
        /// A Check armed at dispatch carries no branch, and Build and UnitTest run in an isolated
        /// checkout resolved from the record's branch and commit. Executing one unstamped measures
        /// the vessel's DEFAULT branch, which is a green that says nothing about the work under
        /// review -- worse than no green, because a gate would honor it. A voyage that has already
        /// completed is left alone: its work is on the default branch by then, so the default branch
        /// is the correct subject.
        /// </remarks>
        internal async Task<CheckRun> StampWorkUnderReviewAsync(CheckRun run, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(run.BranchName)) return run;
            if (String.IsNullOrWhiteSpace(run.VoyageId)) return run;

            Voyage? voyage = await _Database.Voyages.ReadAsync(run.VoyageId, token).ConfigureAwait(false);
            if (voyage == null || voyage.Status == VoyageStatusEnum.Complete) return run;

            Mission? work = await ResolveWorkUnderReviewAsync(run, token).ConfigureAwait(false);
            if (work == null) return run;

            CheckRun? stamped = await CheckRunService.StampPendingSourceAsync(
                _Database,
                run.Id,
                work.BranchName,
                work.CommitHash,
                token).ConfigureAwait(false);
            if (stamped == null) throw new InvalidOperationException("The check run no longer exists.");
            if (stamped.Status == CheckRunStatusEnum.Pending
                && String.Equals(stamped.CommitHash, work.CommitHash, StringComparison.OrdinalIgnoreCase))
            {
                _Logging.Info(_Header + "armed check " + run.Id + " pointed at " + work.BranchName
                    + " (" + work.CommitHash + ") from mission " + work.Id);
            }

            return stamped;
        }

        internal async Task<CheckRun> ExecutePendingAsync(AuthContext auth, CheckRun pending, CancellationToken token)
        {
            pending = await StampWorkUnderReviewAsync(pending, token).ConfigureAwait(false);

            await WriteEventAsync(
                "check.auto_queued",
                "Automated check queued: " + (pending.Label ?? pending.Type.ToString()),
                pending,
                new { pending.Id, pending.Type, pending.Label },
                token).ConfigureAwait(false);

            CheckRun result = await _CheckRuns.RunPendingAsync(auth, pending.Id, token: token).ConfigureAwait(false);

            // A record that did not execute (it waits for its stamp, or was cancelled because its
            // voyage ended unstamped) has no verdict, so it must not be reported as a failure.
            string outcomeEvent = result.Status == CheckRunStatusEnum.Passed
                ? "check.auto_passed"
                : result.Status == CheckRunStatusEnum.Failed
                    ? "check.auto_failed"
                    : "check.auto_not_run";

            await WriteEventAsync(
                outcomeEvent,
                "Automated check " + result.Status + ": " + (result.Label ?? result.Type.ToString()),
                result,
                new { result.Id, result.Type, result.Label, result.Status, result.ExitCode, result.Summary },
                token).ConfigureAwait(false);

            return result;
        }

        internal async Task<bool> IsEligibleAsync(CheckRun run, CancellationToken token)
        {
            if (run.Status != CheckRunStatusEnum.Pending) return false;
            // An operator's run_check or retry already executes this record; the sweep neither runs nor
            // discards it.
            if (CheckRunService.IsOperatorRun(run.Id)) return false;
            if (String.IsNullOrWhiteSpace(run.VesselId)) return false;
            if (run.Type == CheckRunTypeEnum.Deploy || run.Type == CheckRunTypeEnum.Rollback) return false;

            if (!String.IsNullOrWhiteSpace(run.DeploymentId))
                return false;

            if (!String.IsNullOrWhiteSpace(run.VoyageId))
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(run.VoyageId, token).ConfigureAwait(false);
                if (voyage == null) return false;
                if (voyage.Status == VoyageStatusEnum.Complete) return true;
                string? endedReason = VoyageCheckDiscard.ReasonFor(voyage.Status);
                if (endedReason != null)
                {
                    // Never run, never counted: a Check whose voyage ended is discarded with its reason (in its
                    // Summary, its Output and a check.discarded event), so it does not sit Pending forever and
                    // inflate the required-pending count. Covers every path that ends a voyage, including those
                    // that set the status directly.
                    if (await VoyageCheckDiscard.DiscardAsync(_Database, run, endedReason, token).ConfigureAwait(false))
                        _Logging.Info(_Header + "discarded pending check " + run.Id + " of ended voyage " + voyage.Id + " (" + endedReason + ")");
                    return false;
                }

                // Waiting for the voyage to COMPLETE is a condition this Check itself prevents: a
                // Judge PASS requires a green independent Check, so the voyage cannot complete until
                // this record runs, and this record would not run until the voyage completed. The
                // work becomes final much earlier -- once a stage has committed it and no stage that can
                // still commit remains, which is no later than when only the Judge is left -- and that
                // is the point the gate actually needs an answer. Running before then measures a commit
                // a later stage replaces, and that green is superseded without deciding anything.
                List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(run.VoyageId!, token).ConfigureAwait(false);
                // On a voyage that has split, only the record's own lane decides when its work is final.
                if (StaleCheckSupersessionService.SelectFinalWorkUnderReviewFor(run, missions) == null) return false;
                return !IsBehindRunningGate(run, missions);
            }

            if (!String.IsNullOrWhiteSpace(run.MissionId))
            {
                Mission? mission = await _Database.Missions.ReadAsync(run.MissionId, token).ConfigureAwait(false);
                if (mission?.Status != MissionStatusEnum.Complete) return false;
                return !IsBehindRunningGate(run, new List<Mission> { mission! });
            }

            Release? release = await FindLinkedReleaseAsync(run.Id, token).ConfigureAwait(false);
            if (release != null)
                return await IsReleaseReadyForChecksAsync(release, token).ConfigureAwait(false);

            return await IsVesselIdleAsync(run.VesselId!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// True when a definition-of-done gate of one of these missions runs on the check's vessel. The gate runs
        /// the same full build or test suite, so a Build or UnitTest check started beside it doubles the load
        /// for the same answer; it waits, and a gate that passes the same command on the same commit then
        /// satisfies it (see <see cref="DefinitionOfDoneCommandPasses"/>). The wait is recorded for reporting.
        /// </summary>
        private bool IsBehindRunningGate(CheckRun run, List<Mission> missions)
        {
            if (run.Type != CheckRunTypeEnum.Build && run.Type != CheckRunTypeEnum.UnitTest) return false;
            foreach (Mission mission in missions)
            {
                if (!String.Equals(mission.VesselId, run.VesselId, StringComparison.Ordinal)) continue;
                if (!DefinitionOfDoneGateRuns.IsRunning(mission.Id)) continue;
                _GateWaits[run.Id] = mission.Id;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Publish and report the checks the latest scan held back behind a running gate.
        /// </summary>
        private async Task ReportGateOverlapAsync(HostSlotSnapshot slot, CancellationToken token)
        {
            Dictionary<string, string> waits = new Dictionary<string, string>(_GateWaits, StringComparer.Ordinal);
            AutomaticCheckRunPlaces.PublishGateWaits(waits);
            DateTime now = _Clock();
            DefinitionOfDoneSettings settings = _GateSettings?.Invoke() ?? new DefinitionOfDoneSettings();
            await StepWaitAsync(_GateOverlapWait, waits.Count > 0, "gate", now,
                since => Task.FromResult(DescribeGateOverlap(waits, since, now)), slot, settings, now, token).ConfigureAwait(false);
        }

        private static string DescribeGateOverlap(Dictionary<string, string> waits, DateTime since, DateTime now)
        {
            List<string> items = waits
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + " behind the gate of mission " + pair.Value)
                .ToList();
            return "Automatic check(s) waiting for a running definition-of-done gate that runs the same suite on the same vessel for "
                + WaitMinutes(since, now) + " min since " + since.ToString("u") + ": " + String.Join("; ", items) + ".";
        }

        private async Task<bool> IsDeploymentCheckEligibleAsync(CheckRun run, CancellationToken token)
        {
            Deployment? deployment = await _Database.Deployments.ReadAsync(run.DeploymentId!, token: token).ConfigureAwait(false);
            if (deployment == null) return false;

            if (deployment.Status == DeploymentStatusEnum.PendingApproval
                || deployment.Status == DeploymentStatusEnum.Denied
                || deployment.Status == DeploymentStatusEnum.Failed)
                return false;

            if (run.Type == CheckRunTypeEnum.Deploy || run.Type == CheckRunTypeEnum.Rollback)
                return false;

            if (run.Type == CheckRunTypeEnum.RollbackVerification)
                return deployment.Status == DeploymentStatusEnum.RolledBack;

            return deployment.Status == DeploymentStatusEnum.Succeeded
                || deployment.Status == DeploymentStatusEnum.VerificationFailed;
        }

        private async Task<bool> IsReleaseReadyForChecksAsync(Release release, CancellationToken token)
        {
            if (release.Status == ReleaseStatusEnum.Shipped
                || release.Status == ReleaseStatusEnum.Failed
                || release.Status == ReleaseStatusEnum.RolledBack)
                return false;

            List<string> voyageIds = release.VoyageIds ?? new List<string>();
            List<string> missionIds = release.MissionIds ?? new List<string>();

            foreach (string voyageId in voyageIds)
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage?.Status != VoyageStatusEnum.Complete)
                    return false;
            }

            foreach (string missionId in missionIds)
            {
                Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                if (mission?.Status != MissionStatusEnum.Complete)
                    return false;
            }

            return voyageIds.Count > 0 || missionIds.Count > 0;
        }

        private async Task<bool> IsVesselIdleAsync(string vesselId, CancellationToken token)
        {
            foreach (MissionStatusEnum status in new[]
            {
                MissionStatusEnum.Pending,
                MissionStatusEnum.Assigned,
                MissionStatusEnum.InProgress,
                MissionStatusEnum.WorkProduced,
                MissionStatusEnum.LandingFailed,
                MissionStatusEnum.Testing,
                MissionStatusEnum.Review,
                MissionStatusEnum.PullRequestOpen
            })
            {
                List<Mission> missions = await _Database.Missions.EnumerateByStatusAsync(status, token).ConfigureAwait(false);
                if (missions.Any(mission => String.Equals(mission.VesselId, vesselId, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        }

        private async Task RefreshLinkedReleasesAsync(AuthContext auth, CheckRun run, CancellationToken token)
        {
            List<Release> releases = await FindLinkedReleasesAsync(run.Id, token).ConfigureAwait(false);
            foreach (Release release in releases)
            {
                try
                {
                    await _Releases.RefreshAsync(auth, release.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not refresh release " + release.Id + " after check " + run.Id + ": " + ex.Message);
                }
            }
        }

        private async Task<Release?> FindLinkedReleaseAsync(string checkRunId, CancellationToken token)
        {
            List<Release> releases = await FindLinkedReleasesAsync(checkRunId, token).ConfigureAwait(false);
            return releases.FirstOrDefault();
        }

        private async Task<List<Release>> FindLinkedReleasesAsync(string checkRunId, CancellationToken token)
        {
            List<Release> matches = new List<Release>();
            ReleaseQuery query = new ReleaseQuery { PageNumber = 1, PageSize = 200 };
            while (true)
            {
                EnumerationResult<Release> page = await _Database.Releases.EnumerateAsync(query, token).ConfigureAwait(false);
                matches.AddRange(page.Objects.Where(release =>
                    release.CheckRunIds.Contains(checkRunId, StringComparer.OrdinalIgnoreCase)));
                if (page.Objects.Count < query.PageSize) break;
                query.PageNumber++;
            }

            return matches;
        }

        private async Task CreateFailureIncidentAsync(CheckRun run, CancellationToken token)
        {
            string title = "Automated check failed: " + (run.Label ?? run.Type.ToString());
            int recentFailureCount = await CountRecentFailuresAsync(run, token).ConfigureAwait(false);
            AuthContext incidentAuth = AuthContext.Authenticated(
                run.TenantId ?? Constants.DefaultTenantId,
                run.UserId ?? Constants.DefaultUserId,
                isAdmin: false,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Automated checks");

            IncidentUpsertRequest request = new IncidentUpsertRequest
            {
                Title = title,
                Summary = (run.Summary ?? "Automated check failed.")
                    + " Recent failures for this vessel/check type: " + recentFailureCount + ".",
                Status = IncidentStatusEnum.Open,
                Severity = recentFailureCount >= 2 ? IncidentSeverityEnum.High : IncidentSeverityEnum.Medium,
                VesselId = run.VesselId,
                CheckRunId = run.Id,
                MissionId = run.MissionId,
                VoyageId = run.VoyageId,
                DeploymentId = run.DeploymentId,
                EnvironmentName = run.EnvironmentName,
                ReleaseId = (await FindLinkedReleaseAsync(run.Id, token).ConfigureAwait(false))?.Id,
                Impact = "Release or deployment promotion is blocked until the failed check is resolved.",
                RootCause = "Automated check command failed or could not run.",
                RecoveryNotes = "Inspect check run " + run.Id + ", fix the failure, then retry or create a new passing check run."
            };

            try
            {
                await _Incidents.CreateAutomaticallyAsync(incidentAuth, request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not create incident for failed check " + run.Id + ": " + ex.Message);
            }
        }

        private async Task<int> CountRecentFailuresAsync(CheckRun run, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = run.TenantId,
                VesselId = run.VesselId,
                Type = run.Type,
                Status = CheckRunStatusEnum.Failed,
                FromUtc = DateTime.UtcNow.AddDays(-30),
                PageNumber = 1,
                // Only the total is used, and the total comes from a separate count.
                PageSize = 1,
                IncludeOutput = false
            };

            EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            return page.TotalRecords > Int32.MaxValue ? Int32.MaxValue : Convert.ToInt32(page.TotalRecords);
        }

        private async Task WriteEventAsync(string eventType, string message, CheckRun run, object payload, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = run.TenantId,
                UserId = run.UserId,
                EntityType = "check_run",
                EntityId = run.Id,
                MissionId = run.MissionId,
                VesselId = run.VesselId,
                VoyageId = run.VoyageId,
                Payload = JsonSerializer.Serialize(payload, _JsonOptions)
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Automated checks");
        }

        private static int HeldSeconds(DateTime? sinceUtc)
        {
            TimeSpan held = sinceUtc.HasValue ? DateTime.UtcNow - sinceUtc.Value : TimeSpan.Zero;
            return Math.Max(0, (int)held.TotalSeconds);
        }
    }
}
