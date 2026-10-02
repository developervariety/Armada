namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Whether a mission may write native captain memory. An objective forbids it with the tag
    /// <see cref="ForbidTag"/>, or with a rollout constraint or non-goal that says the work writes no
    /// memory or no native memory. The rule covers every stage of the objective's voyages and every
    /// rescue stage descended from them, found through the parent and dependency links. A sentence in a
    /// brief alone does not stop a persona whose job is writing memory; this rule refuses the write.
    /// </summary>
    public static class NativeMemoryPolicy
    {
        #region Public-Members

        /// <summary>Objective tag that forbids native memory writes for its missions.</summary>
        public const string ForbidTag = "no-native-memory";

        #endregion

        #region Private-Members

        private const int _MaxLinkHops = 16;

        private static readonly string[] _ForbidPhrases =
        {
            "no native memory",
            "no native-memory",
            "writes no memory",
            "write no memory",
            "writes no native memory"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether an objective forbids native memory writes for its missions.
        /// </summary>
        /// <param name="objective">Objective; null forbids nothing.</param>
        /// <returns>True when the objective forbids them.</returns>
        public static bool Forbids(Objective? objective)
        {
            if (objective == null) return false;
            if (objective.Tags != null && objective.Tags.Any(tag => String.Equals(tag?.Trim(), ForbidTag, StringComparison.OrdinalIgnoreCase)))
                return true;
            IEnumerable<string> statements = (objective.RolloutConstraints ?? new List<string>())
                .Concat(objective.NonGoals ?? new List<string>());
            return statements.Any(statement => !String.IsNullOrEmpty(statement)
                && _ForbidPhrases.Any(phrase => statement.Contains(phrase, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// The objective that forbids native memory writes for this mission, or null when none does.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="missionId">Calling mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The forbidding objective, or null.</returns>
        public static async Task<Objective?> FindForbiddingObjectiveAsync(DatabaseDriver database, string? missionId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(missionId)) return null;

            List<Objective> objectives = (await database.Objectives.EnumerateAsync(token).ConfigureAwait(false))
                .Where(Forbids)
                .ToList();
            if (objectives.Count == 0) return null;

            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Queue<string> pending = new Queue<string>();
            pending.Enqueue(missionId);
            while (pending.Count > 0 && visited.Count < _MaxLinkHops)
            {
                string id = pending.Dequeue();
                if (!visited.Add(id)) continue;
                Mission? mission = await database.Missions.ReadAsync(id, token).ConfigureAwait(false);
                if (mission == null) continue;

                Objective? owner = objectives.FirstOrDefault(objective =>
                    (objective.MissionIds?.Contains(mission.Id, StringComparer.Ordinal) ?? false)
                    || (!String.IsNullOrEmpty(mission.VoyageId) && (objective.VoyageIds?.Contains(mission.VoyageId, StringComparer.Ordinal) ?? false)));
                if (owner != null) return owner;

                if (!String.IsNullOrWhiteSpace(mission.ParentMissionId)) pending.Enqueue(mission.ParentMissionId!);
                if (!String.IsNullOrWhiteSpace(mission.DependsOnMissionId)) pending.Enqueue(mission.DependsOnMissionId!);
            }
            return null;
        }

        #endregion
    }
}
