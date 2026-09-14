namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Pure, stateless selector that, given a snapshot of objectives, returns the
    /// eligible objectives in dispatch order (Priority ascending then Rank ascending).
    /// No I/O, no DB, no logging -- the testable heart of the autonomous scheduler.
    /// </summary>
    public static class AutonomousObjectiveSelector
    {
        #region Public-Methods

        /// <summary>
        /// Returns the subset of <paramref name="all"/> that are eligible for autonomous
        /// dispatch, sorted by Priority ascending (P0 first) then Rank ascending then Id
        /// for a stable deterministic order.
        /// </summary>
        /// <param name="all">Full snapshot of objectives to evaluate.</param>
        /// <returns>Eligible objectives in dispatch order.</returns>
        public static List<Objective> SelectEligible(IReadOnlyList<Objective> all)
        {
            if (all is null) throw new ArgumentNullException(nameof(all));
            if (all.Count == 0) return new List<Objective>();

            Dictionary<string, Objective> byId = BuildIndex(all);

            List<Objective> eligible = new List<Objective>();
            foreach (Objective obj in all)
            {
                if (IsEligible(obj, byId)) eligible.Add(obj);
            }

            eligible.Sort(CompareDispatchOrder);
            return eligible;
        }

        /// <summary>
        /// Return autonomous-dispatch candidates before dependency readiness is applied. The
        /// scheduler uses this form when the shared dispatch preview is available, so blocked
        /// objectives reach the diagnostic path instead of disappearing before a reason is made.
        /// </summary>
        public static List<Objective> SelectCandidates(IReadOnlyList<Objective> all)
        {
            if (all is null) throw new ArgumentNullException(nameof(all));

            List<Objective> candidates = all.Where(IsCandidate).ToList();
            candidates.Sort(CompareDispatchOrder);
            return candidates;
        }

        #endregion

        #region Private-Methods

        private static Dictionary<string, Objective> BuildIndex(IReadOnlyList<Objective> all)
        {
            Dictionary<string, Objective> index = new Dictionary<string, Objective>(
                all.Count, StringComparer.Ordinal);
            foreach (Objective obj in all) index[obj.Id] = obj;
            return index;
        }

        private static bool IsEligible(Objective obj, Dictionary<string, Objective> index)
        {
            if (!IsCandidate(obj)) return false;

            // Linked voyages do not exclude a row here. Linking a voyage promotes the objective to
            // InProgress, so a Scoped or Planned row that still carries voyage ids is one an
            // operator has requeued after those voyages ended. Whether any of them is still live
            // is a database question, answered by the scheduler before it dispatches; a pure
            // exclusion on the ids would drop every requeue silently, before any skip reason exists.

            foreach (string blockerId in obj.BlockedByObjectiveIds)
            {
                if (!index.TryGetValue(blockerId, out Objective? blocker))
                    return false; // missing blocker -- conservative: treat as blocked

                if (blocker.Status != ObjectiveStatusEnum.Completed)
                    return false;
            }

            return true;
        }

        private static bool IsCandidate(Objective obj)
        {
            return obj.AutoDispatchEnabled
                && !ObjectiveLifecycleRules.IsTerminalStatus(obj.Status)
                && (obj.Status == ObjectiveStatusEnum.Scoped || obj.Status == ObjectiveStatusEnum.Planned);
        }

        private static int CompareDispatchOrder(Objective left, Objective right)
        {
            int priorityDelta = (int)left.Priority - (int)right.Priority;
            if (priorityDelta != 0) return priorityDelta;

            int rankDelta = left.Rank - right.Rank;
            if (rankDelta != 0) return rankDelta;

            return String.Compare(left.Id, right.Id, StringComparison.Ordinal);
        }

        #endregion
    }
}
