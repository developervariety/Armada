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
            if (!obj.AutoDispatchEnabled) return false;

            if (obj.Status == ObjectiveStatusEnum.Completed || obj.Status == ObjectiveStatusEnum.Cancelled)
                return false;

            if (obj.Status != ObjectiveStatusEnum.Scoped && obj.Status != ObjectiveStatusEnum.Planned)
                return false;

            // Any linked voyage means this objective was already dispatched (or is awaiting
            // reconcile). Re-dispatch is owned by AutonomousRecoveryOrchestrator, not the scheduler.
            if (obj.VoyageIds.Count > 0)
                return false;

            foreach (string blockerId in obj.BlockedByObjectiveIds)
            {
                if (!index.TryGetValue(blockerId, out Objective? blocker))
                    return false; // missing blocker -- conservative: treat as blocked

                if (blocker.Status != ObjectiveStatusEnum.Completed)
                    return false;
            }

            return true;
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
