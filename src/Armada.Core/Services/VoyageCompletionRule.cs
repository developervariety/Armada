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
    /// The single rule for when a voyage leaves Open or InProgress for Complete or Failed. Every
    /// writer of voyage completion calls <see cref="ApplyAsync"/> and writes nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule runs three steps, in order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// Terminal-voyage guard. A voyage in the voyage terminal set (Complete, Failed, Cancelled; see
    /// <see cref="TerminalVoyageMissionRule.IsTerminalVoyage"/>) is never rewritten by completion.
    /// This set belongs to the voyage lifecycle only; a mission, merge entry, landing job, Check run
    /// and objective each have their own terminal set.
    /// </description></item>
    /// <item><description>
    /// Mission-state evaluation. A voyage with no missions is kept. A voyage with any mission that is
    /// not done (<see cref="IsMissionDone"/>) is kept. A voyage with produced work held for an operator
    /// decision is kept: the held mission lands or fails only when the operator clears or fails the
    /// hold. When every mission is done, the voyage is Failed if any mission failed
    /// (<see cref="IsMissionFailed"/>); otherwise it is a completion candidate.
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

        /// <summary>Complete: every mission is done, none failed, and no Check holds or fails it.</summary>
        public const string ReasonAllDone = "all_done";

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

            if (TerminalVoyageMissionRule.IsTerminalVoyage(voyage.Status))
                return VoyageCompletionVerdict.Keep(ReasonVoyageTerminal);

            if (missions.Count == 0)
                return VoyageCompletionVerdict.Keep(ReasonNoMissions);

            if (missions.Any(m => !IsMissionDone(m.Status)))
                return VoyageCompletionVerdict.Keep(ReasonMissionActive);

            if (missions.Any(IsHeldForOperatorReview))
                return VoyageCompletionVerdict.Keep(ReasonOperatorReviewHold);

            if (missions.Any(m => IsMissionFailed(m.Status)))
                return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Failed, ReasonMissionFailed);

            if (!VoyageReportOnlyClassifier.IsFullyReportOnly(missions))
            {
                CheckGate gate = await EvaluateChecksAsync(database, voyage.TenantId, voyage.Id, missions, token).ConfigureAwait(false);
                if (gate == CheckGate.HasFailed)
                    return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Failed, ReasonCheckFailed);
                if (gate == CheckGate.HasPending)
                    return VoyageCompletionVerdict.Keep(ReasonChecksPending);
            }

            return VoyageCompletionVerdict.Finish(VoyageStatusEnum.Complete, ReasonAllDone);
        }

        /// <summary>
        /// Read the voyage and its missions, decide with <see cref="EvaluateAsync"/>, and write the
        /// terminal status when the verdict finishes the voyage. The voyage is read immediately before
        /// the decision, so a voyage that reached the terminal set since the caller last looked is kept.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verdict and the voyage as written, or as read when it was kept.</returns>
        public static async Task<VoyageCompletionResult> ApplyAsync(
            DatabaseDriver database,
            string? voyageId,
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
            return new VoyageCompletionResult(voyage, verdict);
        }

        #endregion

        #region Private-Methods

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
            List<CheckRun> active = checks.Values
                .Where(c => CheckRunGateRules.ParticipatesInRealSignalGate(c, workCommit)).ToList();
            if (active.Count == 0) return CheckGate.NoChecks;
            if (active.Any(c => c.Status == CheckRunStatusEnum.Failed)) return CheckGate.HasFailed;
            if (active.Any(c => CheckRunGateRules.IsUnresolved(c, workCommit))) return CheckGate.HasPending;
            return CheckGate.AllGreen;
        }

        #endregion
    }
}
