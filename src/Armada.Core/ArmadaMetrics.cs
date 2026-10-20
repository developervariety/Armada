namespace Armada.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Metrics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;

    /// <summary>
    /// Armada's telemetry instruments. Emitting a measurement rides the .NET base class library
    /// (<see cref="Meter"/>) and costs nothing until a host subscribes, so Armada.Core takes no
    /// dependency on any telemetry framework. A host (see the server's telemetry wiring) observes
    /// <see cref="MeterName"/> and exports to Prometheus/Grafana/Loki. Instruments focus on the
    /// reliability signals that matter operationally: mission failures, dock churn, stalls,
    /// recoveries, review timeouts, handoff repair, merge-queue processing, and the duration of each
    /// source-control phase a mission pays for (repository-lock wait, fetch, checkout, artifact copy,
    /// host-slot wait, validation and landing). Units are left unset so the OpenTelemetry Prometheus
    /// exporter yields clean "armada_*_total" and "armada_*_seconds_bucket" series names; every
    /// duration instrument therefore carries its unit in its name and records seconds.
    /// </summary>
    /// <remarks>
    /// Labels are a closed vocabulary defined here: phase kinds, outcomes and a retried flag. A label
    /// never carries a path, a branch, a commit, a credential or a mission, voyage or vessel id, so the
    /// series count stays bounded however many missions run. Recording never changes the result of the
    /// operation it measures: a listener that throws is counted in <see cref="RecordingFailures"/> and
    /// the operation continues.
    /// </remarks>
    public static class ArmadaMetrics
    {
        #region Private-Members

        // This region sits first because static fields initialize in text order and every instrument
        // below is created from the meter and the bucket boundaries.
        private static readonly Meter _Meter = new Meter(MeterName);

#if NET9_0_OR_GREATER
        // Bucket boundaries in seconds, from a lock that is free at once to a validation run of an hour.
        // Older runtimes take the exporter's default boundaries.
        private static readonly double[] _SecondsBuckets = new double[]
        {
            0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600
        };
#endif

        private static long _RecordingFailures = 0;

        #endregion

        #region Public-Members

        /// <summary>
        /// The meter name a telemetry host subscribes to.
        /// </summary>
        public const string MeterName = "Armada";

        /// <summary>Label key naming which lock a wait was for.</summary>
        public const string TagLock = "lock";

        /// <summary>Label key naming how an operation ended.</summary>
        public const string TagOutcome = "outcome";

        /// <summary>Label key naming the phase variant (checkout kind, validation kind).</summary>
        public const string TagKind = "kind";

        /// <summary>Label key naming the landing path.</summary>
        public const string TagPath = "path";

        /// <summary>Label key that is "true" when the operation retried at least once.</summary>
        public const string TagRetried = "retried";

        /// <summary>Lock value: the per-vessel slot every landing ref move takes.</summary>
        public const string LockVesselLanding = "vessel_landing";

        /// <summary>Lock value: the per-repository slot a dock provision takes for its own repository.</summary>
        public const string LockDockProvision = "dock_provision";

        /// <summary>Lock value: the per-repository slot a dock provision takes for a sibling repository.</summary>
        public const string LockSiblingProvision = "sibling_provision";

        /// <summary>Lock value: the per-repository slot the git service takes to add a worktree.</summary>
        public const string LockGitWorktree = "git_worktree";

        /// <summary>Checkout kind: the dock's own worktree.</summary>
        public const string CheckoutPrimary = "primary";

        /// <summary>Checkout kind: a declared sibling repository beside the dock.</summary>
        public const string CheckoutSibling = "sibling";

        /// <summary>Validation kind: a definition-of-done gate command.</summary>
        public const string ValidationDefinitionOfDone = "definition_of_done";

        /// <summary>Validation kind: a check run command.</summary>
        public const string ValidationCheck = "check";

        /// <summary>Validation kind: a merge-queue test command.</summary>
        public const string ValidationMergeQueueTest = "merge_queue_test";

        /// <summary>Landing path: a mission merged in a dedicated integration worktree.</summary>
        public const string LandingLocalMerge = "local_merge";

        /// <summary>Landing path: a merge-queue entry processed to a terminal state.</summary>
        public const string LandingMergeQueue = "merge_queue";

        /// <summary>Outcome: the operation completed.</summary>
        public const string OutcomeSuccess = "success";

        /// <summary>Outcome: the operation failed.</summary>
        public const string OutcomeFailure = "failure";

        /// <summary>Outcome: the operation was cancelled.</summary>
        public const string OutcomeCanceled = "canceled";

        /// <summary>Outcome: a lock was acquired.</summary>
        public const string OutcomeAcquired = "acquired";

        /// <summary>Outcome: a host slot was granted.</summary>
        public const string OutcomeGranted = "granted";

        /// <summary>Outcome: an existing checkout was reused instead of created.</summary>
        public const string OutcomeReused = "reused";

        /// <summary>Outcome: an artifact tree was copied into an empty destination.</summary>
        public const string OutcomeCopied = "copied";

        /// <summary>Outcome: a stale artifact tree was replaced.</summary>
        public const string OutcomeRefreshed = "refreshed";

        /// <summary>Outcome: an artifact tree already matched its source and was not copied.</summary>
        public const string OutcomeCurrent = "current";

        /// <summary>Outcome: a validation command exited 0.</summary>
        public const string OutcomePassed = "passed";

        /// <summary>Outcome: a validation command exited non-zero.</summary>
        public const string OutcomeFailed = "failed";

        /// <summary>Outcome: a validation command ran past its timeout.</summary>
        public const string OutcomeTimeout = "timeout";

        /// <summary>Outcome: a validation command could not be started or completed.</summary>
        public const string OutcomeError = "error";

        /// <summary>Outcome: the work reached the target branch.</summary>
        public const string OutcomeLanded = "landed";

        /// <summary>Outcome: the work landed but a step after the landing failed.</summary>
        public const string OutcomeLandedPostStepFailed = "landed_post_step_failed";

        /// <summary>Outcome: a merge-queue entry was routed to a pull request.</summary>
        public const string OutcomePullRequestOpen = "pull_request_open";

        /// <summary>Outcome: a merge-queue pass ended before the entry reached a terminal state.</summary>
        public const string OutcomeIncomplete = "incomplete";

        /// <summary>Missions that reached Failed.</summary>
        public static readonly Counter<long> MissionsFailed =
            _Meter.CreateCounter<long>("armada.missions.failed", null, "Missions failed");

        /// <summary>Docks provisioned. Added once per dock a provision returns.</summary>
        public static readonly Counter<long> DocksProvisioned =
            _Meter.CreateCounter<long>("armada.docks.provisioned", null, "Docks provisioned");

        /// <summary>Docks reclaimed. Added once per dock a reclaim moves to inactive.</summary>
        public static readonly Counter<long> DocksReclaimed =
            _Meter.CreateCounter<long>("armada.docks.reclaimed", null, "Docks reclaimed");

        /// <summary>Captain stalls detected.</summary>
        public static readonly Counter<long> CaptainStalls =
            _Meter.CreateCounter<long>("armada.captains.stalls", null, "Captain stalls detected");

        /// <summary>Captain recovery attempts.</summary>
        public static readonly Counter<long> CaptainRecoveries =
            _Meter.CreateCounter<long>("armada.captains.recoveries", null, "Captain recovery attempts");

        /// <summary>Missions force-failed for exceeding max runtime.</summary>
        public static readonly Counter<long> MissionRuntimeExceeded =
            _Meter.CreateCounter<long>("armada.missions.runtime_exceeded", null, "Missions force-failed for exceeding max runtime");

        /// <summary>Reviews released after going overdue.</summary>
        public static readonly Counter<long> ReviewsOverdue =
            _Meter.CreateCounter<long>("armada.reviews.overdue", null, "Reviews released after going overdue");

        /// <summary>Dangling pipeline handoffs re-driven.</summary>
        public static readonly Counter<long> HandoffsRedriven =
            _Meter.CreateCounter<long>("armada.handoffs.redriven", null, "Dangling pipeline handoffs re-driven");

        /// <summary>
        /// Merge-queue entries processed to a terminal state, labelled with that state
        /// (<see cref="TagOutcome"/>: landed, failed, canceled, pull_request_open).
        /// </summary>
        public static readonly Counter<long> MergeEntriesProcessed =
            _Meter.CreateCounter<long>("armada.mergequeue.processed", null, "Merge-queue entries processed");

        /// <summary>Bytes of extraction artifacts written into sibling checkouts, labelled copied or refreshed.</summary>
        public static readonly Counter<long> ArtifactBytesCopied =
            _Meter.CreateCounter<long>("armada.dock.artifact_copy.bytes", null, "Extraction artifact bytes copied into sibling checkouts");

        /// <summary>Seconds spent waiting for a repository or vessel lock, labelled by lock and outcome.</summary>
        public static readonly Histogram<double> RepositoryLockWait =
            CreateSecondsHistogram("armada.git.repository_lock.wait.seconds", "Seconds waiting for a repository lock");

        /// <summary>Seconds one repository fetch took, including the worktree prune before it, labelled by outcome and retried.</summary>
        public static readonly Histogram<double> FetchDuration =
            CreateSecondsHistogram("armada.git.fetch.seconds", "Seconds per repository fetch");

        /// <summary>
        /// Seconds one dock checkout took, labelled by kind (primary or sibling), outcome and retried. The
        /// primary checkout is the worktree add; a sibling checkout runs from the resolved sibling repository
        /// through its worktree and artifact copy, so it excludes the sibling fetch.
        /// </summary>
        public static readonly Histogram<double> CheckoutDuration =
            CreateSecondsHistogram("armada.dock.checkout.seconds", "Seconds per dock checkout");

        /// <summary>Seconds one extraction-artifact tree took to compare and copy into a sibling checkout, labelled by outcome.</summary>
        public static readonly Histogram<double> ArtifactCopyDuration =
            CreateSecondsHistogram("armada.dock.artifact_copy.seconds", "Seconds per extraction artifact copy");

        /// <summary>Seconds a request waited for a host-wide command slot, labelled by outcome.</summary>
        public static readonly Histogram<double> HostSlotWait =
            CreateSecondsHistogram("armada.hostslot.wait.seconds", "Seconds waiting for a host-wide command slot");

        /// <summary>Seconds one validation command ran, labelled by kind and outcome. The slot wait is not included.</summary>
        public static readonly Histogram<double> ValidationDuration =
            CreateSecondsHistogram("armada.validation.seconds", "Seconds per validation command");

        /// <summary>Seconds one landing took end to end, retries included, labelled by path, outcome and retried.</summary>
        public static readonly Histogram<double> LandingDuration =
            CreateSecondsHistogram("armada.landing.seconds", "Seconds per landing");

        /// <summary>
        /// Measurements a listener refused by throwing. The measured operation continued each time.
        /// </summary>
        public static long RecordingFailures => Interlocked.Read(ref _RecordingFailures);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record a lock wait.
        /// </summary>
        /// <param name="lockName">One of the Lock* values.</param>
        /// <param name="elapsed">Time from the request to the grant or the cancellation.</param>
        /// <param name="acquired">True when the lock was acquired, false when the wait was cancelled.</param>
        public static void RecordRepositoryLockWait(string lockName, TimeSpan elapsed, bool acquired)
        {
            Record(
                RepositoryLockWait,
                elapsed,
                new KeyValuePair<string, object?>(TagLock, lockName),
                new KeyValuePair<string, object?>(TagOutcome, acquired ? OutcomeAcquired : OutcomeCanceled));
        }

        /// <summary>
        /// Wait for a lock slot and record the wait under <paramref name="lockName"/>. A cancelled wait is
        /// recorded as canceled and the cancellation is rethrown unchanged.
        /// </summary>
        /// <param name="slot">The lock to wait for.</param>
        /// <param name="lockName">One of the Lock* values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the slot is held.</returns>
        public static async Task WaitForLockAsync(SemaphoreSlim slot, string lockName, CancellationToken token)
        {
            if (slot == null) throw new ArgumentNullException(nameof(slot));
            Stopwatch wait = Stopwatch.StartNew();
            try
            {
                await slot.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RecordRepositoryLockWait(lockName, wait.Elapsed, false);
                throw;
            }

            RecordRepositoryLockWait(lockName, wait.Elapsed, true);
        }

        /// <summary>
        /// Record one fetch.
        /// </summary>
        /// <param name="elapsed">Duration of the whole fetch call.</param>
        /// <param name="outcome">Success, failure or canceled.</param>
        /// <param name="retried">True when the full fetch was refused and the narrower fetch ran.</param>
        public static void RecordFetch(TimeSpan elapsed, string outcome, bool retried)
        {
            Record(
                FetchDuration,
                elapsed,
                new KeyValuePair<string, object?>(TagOutcome, outcome),
                new KeyValuePair<string, object?>(TagRetried, retried ? "true" : "false"));
        }

        /// <summary>
        /// Record one dock checkout.
        /// </summary>
        /// <param name="kind">Primary or sibling.</param>
        /// <param name="elapsed">Duration of the checkout.</param>
        /// <param name="outcome">Success, reused, failure or canceled.</param>
        /// <param name="retried">True when the checkout ran a second time after releasing a stale holder.</param>
        public static void RecordCheckout(string kind, TimeSpan elapsed, string outcome, bool retried)
        {
            Record(
                CheckoutDuration,
                elapsed,
                new KeyValuePair<string, object?>(TagKind, kind),
                new KeyValuePair<string, object?>(TagOutcome, outcome),
                new KeyValuePair<string, object?>(TagRetried, retried ? "true" : "false"));
        }

        /// <summary>
        /// Record one extraction-artifact tree and, when it was written, the bytes it wrote.
        /// </summary>
        /// <param name="elapsed">Duration of the comparison and any copy.</param>
        /// <param name="outcome">Copied, refreshed, current or failure.</param>
        /// <param name="bytesWritten">Bytes written into the sibling checkout; 0 when nothing was written.</param>
        public static void RecordArtifactCopy(TimeSpan elapsed, string outcome, long bytesWritten)
        {
            KeyValuePair<string, object?> outcomeTag = new KeyValuePair<string, object?>(TagOutcome, outcome);
            Record(ArtifactCopyDuration, elapsed, outcomeTag);
            if (bytesWritten <= 0 || !ArtifactBytesCopied.Enabled) return;
            try
            {
                ArtifactBytesCopied.Add(bytesWritten, outcomeTag);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _RecordingFailures);
            }
        }

        /// <summary>
        /// Record one host-wide slot wait.
        /// </summary>
        /// <param name="elapsed">Time from the request to the grant or the cancellation.</param>
        /// <param name="granted">True when the slot was granted, false when the request was cancelled.</param>
        public static void RecordHostSlotWait(TimeSpan elapsed, bool granted)
        {
            Record(
                HostSlotWait,
                elapsed,
                new KeyValuePair<string, object?>(TagOutcome, granted ? OutcomeGranted : OutcomeCanceled));
        }

        /// <summary>
        /// Record one validation command from its bounded process result.
        /// </summary>
        /// <param name="kind">One of the Validation* values.</param>
        /// <param name="result">The command's result; its duration and its end decide the measurement.</param>
        public static void RecordValidation(string kind, BoundedProcessResult result)
        {
            if (result == null) return;
            string outcome;
            if (result.Cancelled) outcome = OutcomeCanceled;
            else if (result.TimedOut) outcome = OutcomeTimeout;
            else if (result.ExitCode == 0) outcome = OutcomePassed;
            else outcome = OutcomeFailed;
            RecordValidation(kind, result.Duration, outcome);
        }

        /// <summary>
        /// Record one validation command.
        /// </summary>
        /// <param name="kind">One of the Validation* values.</param>
        /// <param name="elapsed">Duration of the command.</param>
        /// <param name="outcome">Passed, failed, timeout, canceled or error.</param>
        public static void RecordValidation(string kind, TimeSpan elapsed, string outcome)
        {
            Record(
                ValidationDuration,
                elapsed,
                new KeyValuePair<string, object?>(TagKind, kind),
                new KeyValuePair<string, object?>(TagOutcome, outcome));
        }

        /// <summary>
        /// Record one landing.
        /// </summary>
        /// <param name="path">Local merge or merge queue.</param>
        /// <param name="elapsed">Duration of the landing, retries included.</param>
        /// <param name="outcome">How the landing ended.</param>
        /// <param name="retried">True when the landing ran more than one attempt.</param>
        public static void RecordLanding(string path, TimeSpan elapsed, string outcome, bool retried)
        {
            Record(
                LandingDuration,
                elapsed,
                new KeyValuePair<string, object?>(TagPath, path),
                new KeyValuePair<string, object?>(TagOutcome, outcome),
                new KeyValuePair<string, object?>(TagRetried, retried ? "true" : "false"));
        }

        /// <summary>
        /// Count one merge-queue entry that reached a terminal state.
        /// </summary>
        /// <param name="outcome">The terminal state in snake case.</param>
        public static void RecordMergeEntryProcessed(string outcome)
        {
            AddOne(MergeEntriesProcessed, new KeyValuePair<string, object?>(TagOutcome, outcome));
        }

        /// <summary>
        /// Count one provisioned dock.
        /// </summary>
        public static void RecordDockProvisioned()
        {
            AddOne(DocksProvisioned);
        }

        /// <summary>
        /// Count one reclaimed dock.
        /// </summary>
        public static void RecordDockReclaimed()
        {
            AddOne(DocksReclaimed);
        }

        #endregion

        #region Private-Methods

        private static Histogram<double> CreateSecondsHistogram(string name, string description)
        {
#if NET9_0_OR_GREATER
            InstrumentAdvice<double> advice = new InstrumentAdvice<double> { HistogramBucketBoundaries = _SecondsBuckets };
            return _Meter.CreateHistogram<double>(name, null, description, null, advice);
#else
            return _Meter.CreateHistogram<double>(name, null, description);
#endif
        }

        private static void Record(Histogram<double> histogram, TimeSpan elapsed, KeyValuePair<string, object?> first)
        {
            if (!histogram.Enabled) return;
            try
            {
                histogram.Record(Seconds(elapsed), first);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _RecordingFailures);
            }
        }

        private static void Record(Histogram<double> histogram, TimeSpan elapsed, KeyValuePair<string, object?> first, KeyValuePair<string, object?> second)
        {
            if (!histogram.Enabled) return;
            try
            {
                histogram.Record(Seconds(elapsed), first, second);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _RecordingFailures);
            }
        }

        private static void Record(
            Histogram<double> histogram,
            TimeSpan elapsed,
            KeyValuePair<string, object?> first,
            KeyValuePair<string, object?> second,
            KeyValuePair<string, object?> third)
        {
            if (!histogram.Enabled) return;
            try
            {
                histogram.Record(Seconds(elapsed), first, second, third);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _RecordingFailures);
            }
        }

        private static void AddOne(Counter<long> counter)
        {
            if (!counter.Enabled) return;
            try
            {
                counter.Add(1);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _RecordingFailures);
            }
        }

        private static void AddOne(Counter<long> counter, KeyValuePair<string, object?> tag)
        {
            if (!counter.Enabled) return;
            try
            {
                counter.Add(1, tag);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _RecordingFailures);
            }
        }

        private static double Seconds(TimeSpan elapsed)
        {
            return elapsed < TimeSpan.Zero ? 0 : elapsed.TotalSeconds;
        }

        #endregion
    }
}
