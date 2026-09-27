namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule for when a voyage ends as Complete or Failed. Every writer of voyage
    /// completion calls <see cref="ApplyAsync"/>, which writes the answer, records the
    /// <c>voyage.completed</c> event for a Complete voyage, and raises the voyage completion hook,
    /// so the writers cannot differ in any of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule runs three steps, in order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// Terminal-voyage guard. The voyage terminal set is Complete, Failed and Cancelled (see
    /// <see cref="TerminalVoyageMissionRule.IsTerminalVoyage"/>). This set belongs to the voyage
    /// lifecycle only; a mission, merge entry, landing job, Check run and objective each have their
    /// own terminal set. A Complete or Cancelled voyage is never rewritten. A Failed voyage moves only
    /// to Complete, and only when the next two steps would complete it and at least one mission is
    /// Complete: its failed work was later landed, for example by a retried landing. A failed stage
    /// that was cancelled instead is not evidence of landed work. A Failed voyage is never written Failed again, so its
    /// completion time stays, and it never returns to Open or InProgress.
    /// </description></item>
    /// <item><description>
    /// Mission-state evaluation. A voyage with no missions is kept. A voyage with any mission that is
    /// not done (<see cref="IsMissionDone"/>) is kept. A voyage with produced work held for an operator
    /// decision is kept: the held mission lands or fails only when the operator clears or fails the
    /// hold. When every mission is done, the voyage is Failed if any mission failed
    /// (<see cref="IsMissionFailed"/>). A voyage whose every mission was cancelled is Cancelled. A voyage
    /// with a cancelled stage and no Complete mission is Failed: its pipeline was cut short and nothing
    /// landed. Otherwise it is a completion candidate.
    /// </description></item>
    /// <item><description>
    /// Check gate. A completion candidate that is not fully report-only is decided by its Checks:
    /// a failed Check makes the voyage Failed, an unresolved Check keeps it, and green or absent
    /// Checks let it complete. A Judge PASS is the agent's own report; the Checks are the real signal.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class VoyageCompletionRule
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded once when this rule writes a voyage Complete.
        /// </summary>
        public const string VoyageCompletedEventType = "voyage.completed";

        /// <summary>Kept: the voyage does not exist.</summary>
        public const string ReasonVoyageMissing = "voyage_missing";

        /// <summary>Kept: the voyage is already in the voyage terminal set.</summary>
        public const string ReasonVoyageTerminal = "voyage_terminal";

        /// <summary>Kept: the voyage has no missions.</summary>
        public const string ReasonNoMissions = "no_missions";

        /// <summary>Kept: at least one mission is still active.</summary>
        public const string ReasonMissionActive = "mission_active";

        /// <summary>Kept: a produced mission is held for an operator decision.</summary>
        public const string ReasonOperatorReviewHold = "operator_review_hold";

        /// <summary>Kept: every mission is done but a Check on the work is still unresolved.</summary>
        public const string ReasonChecksPending = "checks_pending";

        /// <summary>Failed: every mission is done and at least one failed.</summary>
        public const string ReasonMissionFailed = "mission_failed";

        /// <summary>Failed: every mission is done and a Check failed.</summary>
        public const string ReasonCheckFailed = "check_failed";

        /// <summary>
        /// Failed: every mission is done and none failed, but a stage was cancelled and no mission is Complete, so the
        /// pipeline was cut short and nothing landed.
        /// </summary>
        public const string ReasonNothingLanded = "nothing_landed";

        /// <summary>Cancelled: every mission on the voyage was cancelled.</summary>
        public const string ReasonAllCancelled = "all_cancelled";

        /// <summary>Complete: every mission is done, none failed, and no Check holds or fails it.</summary>
        public const string ReasonAllDone = "all_done";

        /// <summary>
        /// Complete: a Failed voyage whose missions are now all done without failure, at least one of them Complete
        /// (its work landed), and whose Checks are green or absent.
        /// </summary>
        public const string ReasonFailedVoyageLanded = "failed_voyage_landed";

        /// <summary>
        /// How long after a voyage ended Failed the periodic sweeps still visit it for
        /// <see cref="ReasonFailedVoyageLanded"/>. The mission path applies the rule to its own voyage
        /// at any age; the sweeps bound their scan so a long history of Failed voyages is not re-read
        /// every cycle.
        /// </summary>
        public static readonly TimeSpan FailedVoyageSweepWindow = TimeSpan.FromHours(24);

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a mission status lets its voyage finish. WorkProduced counts as done: the stage
        /// handed its work on, and any later stage is itself a mission on the voyage. Every other
        /// status (Pending, Assigned, InProgress, Testing, Review, PullRequestOpen) has work ahead of it.
        /// </summary>
        /// <param name="status">Mission status.</param>
        /// <returns>True for Complete, Failed, Cancelled, LandingFailed and WorkProduced.</returns>
        public static bool IsMissionDone(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled
                || status == MissionStatusEnum.LandingFailed
                || status == MissionStatusEnum.WorkProduced;
        }

        /// <summary>
        /// True when a done mission makes its voyage Failed.
        /// </summary>
        /// <param name="status">Mission status.</param>
        /// <returns>True for Failed and LandingFailed.</returns>
        public static bool IsMissionFailed(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Failed || status == MissionStatusEnum.LandingFailed;
        }

        /// <summary>
        /// Decide what completion does to a voyage, without writing anything.
        /// </summary>
        /// <param name="database">Database driver, used to read the voyage's Checks.</param>
        /// <param name="voyage">The voyage as currently stored.</param>
        /// <param name="missions">Every mission on the voyage.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verdict. <see cref="VoyageCompletionVerdict.NewStatus"/> is null when the voyage is kept.</returns>
        public static async Task<VoyageCompletionVerdict> EvaluateAsync(
            DatabaseDriver database,
            Voyage voyage,
            IReadOnlyList<Mission> missions,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (missions == null) throw new ArgumentNullException(nameof(missions));

            if (voyage.Status == VoyageStatusEnum.Complete || voyage.Status == VoyageStatusEnum.Cancelled)
                return VoyageCompletionVerdict.Keep(ReasonVoyageTerminal);

            VoyageCompletionVerdict verdict = await EvaluateMissionsAndChecksAsync(database, voyage.TenantId, voyage.Id, missions, token).ConfigureAwait(false);
            if (voyage.Status != VoyageStatusEnum.Failed) return verdict;

            // A Failed voyage completes only on evidence that work landed: a Complete mission. A failed stage that was
            // later cancelled, or work that was produced but never landed, leaves it Failed.
            return verdict.NewStatus == VoyageStatusEnum.Complete && missions.Any(m => m.Status == MissionStatusEnum.Complete)
                ? VoyageCompletionVerdict.Finish(VoyageStatusEnum.Complete, ReasonFailedVoyageLanded)
                : VoyageCompletionVerdict.Keep(ReasonVoyageTerminal);
        }

        /// <summary>
        /// True when a periodic sweep should visit a voyage: it is Open or InProgress, or it ended
        /// Failed within <see cref="FailedVoyageSweepWindow"/>.
        /// </summary>
        /// <param name="voyage">Voyage.</param>
        /// <param name="nowUtc">Current time.</param>
        /// <returns>True when the sweep applies the rule to the voyage.</returns>
        public static bool IsSweepCandidate(Voyage voyage, DateTime nowUtc)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (voyage.Status == VoyageStatusEnum.Open || voyage.Status == VoyageStatusEnum.InProgress) return true;
            if (voyage.Status != VoyageStatusEnum.Failed) return false;
            DateTime endedUtc = voyage.CompletedUtc ?? voyage.LastUpdateUtc;
            return nowUtc - endedUtc <= FailedVoyageSweepWindow;
        }

        /// <summary>
        /// Read the voyage and its missions, decide with <see cref="EvaluateAsync"/>, write the
        /// terminal status when the verdict ends the voyage, record <see cref="VoyageCompletedEventType"/>
        /// when that status is Complete, and then raise
        /// <paramref name="onVoyageComplete"/> once for that write. The voyage is read immediately
        /// before the decision, so a voyage that another writer ended since the caller last looked is
        /// judged as it is now.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="onVoyageComplete">Voyage completion hook, raised only when this call wrote a terminal status.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verdict, the voyage as written or as read, and any exception the event write or the hook threw.</returns>
        public static async Task<VoyageCompletionResult> ApplyAsync(
            DatabaseDriver database,
            string? voyageId,
            Func<Voyage, Task>? onVoyageComplete,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(voyageId))
                return new VoyageCompletionResult(null, VoyageCompletionVerdict.Keep(ReasonVoyageMissing));

            Voyage? voyage = await database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            if (voyage == null)
                return new VoyageCompletionResult(null, VoyageCompletionVerdict.Keep(ReasonVoyageMissing));

            List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            VoyageCompletionVerdict verdict = await EvaluateAsync(database, voyage, missions, token).ConfigureAwait(false);
            if (verdict.NewStatus == null)
                return new VoyageCompletionResult(voyage, verdict);

            DateTime now = DateTime.UtcNow;
            voyage.Status = verdict.NewStatus.Value;
            voyage.CompletedUtc = now;
            voyage.LastUpdateUtc = now;
            await database.Voyages.UpdateAsync(voyage, token).ConfigureAwait(false);

            Exception? eventException = null;
            if (voyage.Status == VoyageStatusEnum.Complete)
            {
                try
                {
                    await RecordCompletedEventAsync(database, voyage, verdict.Reason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The write stands; the caller logs the event failure with its own header.
                    eventException = ex;
                }
            }

            Exception? hookException = null;
            if (onVoyageComplete != null)
            {
                try
                {
                    await onVoyageComplete(voyage).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The write stands; the caller logs the hook failure with its own header.
                    hookException = ex;
                }
            }

            return new VoyageCompletionResult(voyage, verdict, hookException, eventException);
        }

        #endregion

        #region Private-Methods

        private static async Task RecordCompletedEventAsync(DatabaseDriver database, Voyage voyage, string reason)
        {
            ArmadaEvent evt = new ArmadaEvent(VoyageCompletedEventType, "Voyage completed: " + voyage.Title + " (" + reason + ")");
            evt.TenantId = voyage.TenantId;
            evt.UserId = voyage.UserId;
            evt.EntityType = "voyage";
            evt.EntityId = voyage.Id;
            evt.VoyageId = voyage.Id;
            // The completion is already written, so its record is written even if the caller's request ends.
            await database.Events.CreateAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<VoyageCompletionVerdict> EvaluateMissionsAndChecksAsync(
            DatabaseDriver database, string? tenantId, string voyageId, IReadOnlyList<Mission> missions, CancellationToken token)
        {
            if (missions.Count == 0)
                return VoyageCompletionVerdict.Keep(ReasonNoMissions);

            if (missions.Any(m => !IsMissionDone(m.Status)))
                return VoyageCompletionVerdict.Keep(ReasonMissionActive);

            if (missions.Any(IsHeldForOperatorReview))
                return VoyageCompletionVerdict.Keep(ReasonOperatorReviewHold);

            if (missions.Any(m => IsMissionFailed(m.Status)))
                return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Failed, ReasonMissionFailed);

            // Cancelled counts as done so a voyage can finish, but it is not success. A voyage whose every mission was
            // cancelled ends Cancelled; one with a cancelled stage and no Complete mission was cut short before
            // anything landed and ends Failed. Work produced with no cancelled stage (a voyage that asked for no
            // landing) still completes.
            if (missions.All(m => m.Status == MissionStatusEnum.Cancelled))
                return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Cancelled, ReasonAllCancelled);
            if (!missions.Any(m => m.Status == MissionStatusEnum.Complete) && missions.Any(m => m.Status == MissionStatusEnum.Cancelled))
                return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Failed, ReasonNothingLanded);

            if (!VoyageReportOnlyClassifier.IsFullyReportOnly(missions))
            {
                CheckGate gate = await EvaluateChecksAsync(database, tenantId, voyageId, missions, token).ConfigureAwait(false);
                if (gate == CheckGate.HasFailed)
                    return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Failed, ReasonCheckFailed);
                if (gate == CheckGate.HasPending)
                    return VoyageCompletionVerdict.Keep(ReasonChecksPending);
            }

            return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Complete, ReasonAllDone);
        }

        private static bool IsHeldForOperatorReview(Mission mission)
        {
            return mission.Status == MissionStatusEnum.WorkProduced && mission.HeldForOperatorReview;
        }

        private enum CheckGate
        {
            NoChecks,
            AllGreen,
            HasFailed,
            HasPending
        }

        // Canceled Checks are ignored. An armed record on a voyage that has committed work is queued
        // work the executor will run, so it holds completion; before any commit it is an inert marker.
        private static async Task<CheckGate> EvaluateChecksAsync(
            DatabaseDriver database, string? tenantId, string voyageId, IReadOnlyList<Mission> missions, CancellationToken token)
        {
            List<CheckRunQuery> queries = new List<CheckRunQuery>
            {
                new CheckRunQuery { VoyageId = voyageId }
            };
            foreach (Mission m in missions) queries.Add(new CheckRunQuery { MissionId = m.Id });
            Dictionary<string, CheckRun> checks = await CheckRunEnumeration
                .ReadAllAsync(database, tenantId, queries, token).ConfigureAwait(false);

            string? workCommit = StaleCheckSupersessionService.SelectWorkUnderReview(missions)?.CommitHash;
            List<CheckRun> active = CheckRunGateRules.SelectLatestPerCheck(checks.Values)
                .Where(c => CheckRunGateRules.ParticipatesInRealSignalGate(c, workCommit)).ToList();
            if (active.Count == 0) return CheckGate.NoChecks;
            if (active.Any(c => c.Status == CheckRunStatusEnum.Failed)) return CheckGate.HasFailed;
            if (active.Any(c => CheckRunGateRules.IsUnresolved(c, workCommit))) return CheckGate.HasPending;
            return CheckGate.AllGreen;
        }

        #endregion
    }
}
