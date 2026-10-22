namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The rule for when a voyage's final integration slice (an Architect plan's last mission, flagged
    /// <see cref="Mission.WaitForVoyageWorkers"/>) may start, shared by dispatch, the assignment blocker report and the
    /// failure cascade. The voyage's missions are grouped into slices: each slice starts at a Worker that is not a rework
    /// and holds every stage and rework hanging off it. Against the other slices, integration
    /// <list type="bullet">
    /// <item>waits while any of their missions is still running, while a WorkProduced mission's completion is still being
    /// decided, or while one still has an unsettled merge-queue entry (its work is landing);</item>
    /// <item>does not run when one of their missions ended Failed or LandingFailed;</item>
    /// <item>leaves out a slice an operator cancelled, and otherwise starts from every commit the other slices produced.</item>
    /// </list>
    /// A WorkProduced mission with no merge-queue entry counts as settled, so manual landing and landing mode None can
    /// never hold integration forever.
    /// </summary>
    public static class IntegrationBarrier
    {
        #region Public-Members

        /// <summary>
        /// Title prefix of a Judge-requested rework Worker.
        /// </summary>
        public const string ReworkTitlePrefix = "[Worker] Rework ";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a mission is a final integration slice: a Worker in a voyage flagged to wait for the other slices, or a
        /// rework of one (reworks stored before the flag was inherited take it from their root Worker).
        /// </summary>
        /// <param name="mission">The mission.</param>
        /// <param name="voyageMissions">Every mission in its voyage.</param>
        /// <returns>True for a final integration slice.</returns>
        /// <exception cref="ArgumentNullException">Thrown when voyageMissions is null.</exception>
        public static bool Applies(Mission mission, IReadOnlyList<Mission> voyageMissions)
        {
            if (voyageMissions == null) throw new ArgumentNullException(nameof(voyageMissions));
            if (mission == null || String.IsNullOrEmpty(mission.VoyageId)) return false;
            if (!PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Worker)) return false;
            if (mission.WaitForVoyageWorkers) return true;
            if (!IsRework(mission)) return false;

            Mission? root = SliceRoot(mission, ById(voyageMissions));
            return root != null && root.WaitForVoyageWorkers;
        }

        /// <summary>
        /// Decide where a final integration slice stands. A failed mission in another slice wins over waiting, so the
        /// integration is stopped as soon as it can no longer succeed.
        /// </summary>
        /// <param name="mission">The integration mission.</param>
        /// <param name="voyageMissions">Every mission in its voyage.</param>
        /// <param name="isCompletionInFlight">Whether a WorkProduced mission's completion is still running, or null.</param>
        /// <param name="activeMergeEntryMissionIds">Reads the IDs of missions with unsettled merge-queue entries; called only when needed.</param>
        /// <returns>The barrier state; <see cref="IntegrationBarrierState.Applies"/> is false for any other mission.</returns>
        /// <exception cref="ArgumentNullException">Thrown when voyageMissions or activeMergeEntryMissionIds is null.</exception>
        public static async Task<IntegrationBarrierState> EvaluateAsync(
            Mission mission,
            IReadOnlyList<Mission> voyageMissions,
            Func<string, bool>? isCompletionInFlight,
            Func<Task<HashSet<string>>> activeMergeEntryMissionIds)
        {
            if (voyageMissions == null) throw new ArgumentNullException(nameof(voyageMissions));
            if (activeMergeEntryMissionIds == null) throw new ArgumentNullException(nameof(activeMergeEntryMissionIds));

            IntegrationBarrierState state = new IntegrationBarrierState();
            if (!Applies(mission, voyageMissions)) return state;
            state.Applies = true;

            Dictionary<string, Mission> byId = ById(voyageMissions);
            string ownRoot = (SliceRoot(mission, byId) ?? mission).Id;

            // The integration's own stages are never its siblings, whatever their recorded dependency.
            HashSet<string> own = new HashSet<string>(StringComparer.Ordinal) { mission.Id };
            bool grew;
            do
            {
                grew = false;
                foreach (Mission candidate in voyageMissions)
                    if (!String.IsNullOrEmpty(candidate.DependsOnMissionId) && own.Contains(candidate.DependsOnMissionId!) && own.Add(candidate.Id)) grew = true;
            } while (grew);

            List<Mission> others = new List<Mission>();
            foreach (Mission candidate in voyageMissions)
            {
                if (own.Contains(candidate.Id)) continue;
                if (PersonaCatalog.Matches(candidate.Persona, PersonaCatalog.Architect)) continue;
                Mission? root = SliceRoot(candidate, byId);
                if (root != null && root.Id == ownRoot) continue;
                others.Add(candidate);
            }

            state.FailedMission = others
                .Where(m => m.Status == MissionStatusEnum.Failed || m.Status == MissionStatusEnum.LandingFailed)
                .OrderBy(m => m.CompletedUtc ?? m.LastUpdateUtc).FirstOrDefault();
            if (state.FailedMission != null) return state;

            Mission? running = others.FirstOrDefault(m => !IsTerminal(m.Status));
            if (running != null)
            {
                state.Waiting = true;
                state.WaitingSummary = "Waiting for the voyage's other implementation pipelines, including reviews and checks, to settle ('"
                    + running.Title + "' is " + running.Status + ").";
                return state;
            }

            Mission? deciding = others.FirstOrDefault(m => m.Status == MissionStatusEnum.WorkProduced && isCompletionInFlight != null && isCompletionInFlight(m.Id));
            if (deciding != null)
            {
                state.Waiting = true;
                state.WaitingSummary = "Waiting while Armada finishes handling '" + deciding.Title + "' (its handoff, rework or landing is being decided).";
                return state;
            }

            if (others.Any(m => m.Status == MissionStatusEnum.WorkProduced))
            {
                HashSet<string> queued = await activeMergeEntryMissionIds().ConfigureAwait(false);
                Mission? landing = others.FirstOrDefault(m => m.Status == MissionStatusEnum.WorkProduced && queued.Contains(m.Id));
                if (landing != null)
                {
                    state.Waiting = true;
                    state.WaitingSummary = "Waiting for '" + landing.Title + "' to finish landing through the merge queue.";
                    return state;
                }
            }

            // A slice an operator cancelled is left out entirely; one cancelled because something failed is not a decision
            // to drop it (the failure itself stops integration above).
            HashSet<string> droppedRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission cancelled in others.Where(m => m.Status == MissionStatusEnum.Cancelled && m.FailureKind != MissionFailureKindEnum.DependencyFailed))
            {
                Mission root = SliceRoot(cancelled, byId) ?? cancelled;
                if (droppedRoots.Add(root.Id)) state.DroppedSlices.Add(root);
            }

            HashSet<string> commits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Mission produced in others
                .Where(m => (m.Status == MissionStatusEnum.WorkProduced || m.Status == MissionStatusEnum.Complete) && !String.IsNullOrWhiteSpace(m.CommitHash))
                .OrderBy(m => m.CompletedUtc ?? m.LastUpdateUtc).ThenBy(m => m.CreatedUtc))
            {
                Mission root = SliceRoot(produced, byId) ?? produced;
                if (droppedRoots.Contains(root.Id)) continue;
                if (commits.Add(produced.CommitHash!.Trim())) state.SettledCommits.Add(produced);
            }

            return state;
        }

        /// <summary>
        /// The first mission of the slice a mission belongs to: the nearest Worker at or above it that is not a rework.
        /// </summary>
        /// <param name="mission">The mission.</param>
        /// <param name="voyageMissions">Every mission in its voyage.</param>
        /// <returns>The slice's first mission, or null when the mission is not part of a slice (a planning stage).</returns>
        /// <exception cref="ArgumentNullException">Thrown when mission or voyageMissions is null.</exception>
        public static Mission? SliceRoot(Mission mission, IReadOnlyList<Mission> voyageMissions)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (voyageMissions == null) throw new ArgumentNullException(nameof(voyageMissions));
            return SliceRoot(mission, ById(voyageMissions));
        }

        #endregion

        #region Private-Methods

        private static Mission? SliceRoot(Mission mission, Dictionary<string, Mission> byId)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Mission? cursor = mission;
            while (cursor != null && seen.Add(cursor.Id))
            {
                if (PersonaCatalog.Matches(cursor.Persona, PersonaCatalog.Worker) && !IsRework(cursor)) return cursor;
                cursor = !String.IsNullOrEmpty(cursor.DependsOnMissionId) && byId.TryGetValue(cursor.DependsOnMissionId!, out Mission? parent) ? parent : null;
            }
            return null;
        }

        private static Dictionary<string, Mission> ById(IReadOnlyList<Mission> missions)
        {
            Dictionary<string, Mission> byId = new Dictionary<string, Mission>(StringComparer.Ordinal);
            foreach (Mission m in missions) byId[m.Id] = m;
            return byId;
        }

        private static bool IsRework(Mission mission)
        {
            return !String.IsNullOrEmpty(mission.Title) && mission.Title.StartsWith(ReworkTitlePrefix, StringComparison.Ordinal);
        }

        private static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled
                || status == MissionStatusEnum.LandingFailed;
        }

        #endregion
    }
}
