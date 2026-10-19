namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// The one definition of the lines of work in a voyage, and of which line a Check record
    /// measures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most voyages run one line of work: each stage depends on the one before it. A planner stage
    /// can split its plan into several lines (an Architect fan-out): two or more later stages depend
    /// on it. Each line commits on its own branch and ends in its own Judge, so a Check that measured
    /// one line says nothing about another. Every gate that reads
    /// voyage-armed Checks asks this class which records belong to which lane: the Judge check gate,
    /// the executor's eligibility and stamping, and the stale-record supersession. They cannot
    /// disagree, because none of them keeps its own copy of the rule.
    /// </para>
    /// <para>
    /// A lane is a leaf of the voyage's <see cref="Mission.DependsOnMissionId"/> graph plus every
    /// stage above it. Stages of one parallel stage group (the same parent and the same
    /// <see cref="Mission.StageOrder"/>) are one step of one line, not separate lines, so a group
    /// member that nothing depends on is not a leaf of its own. A voyage in which no step has more
    /// than one dependent step has one lane holding every stage, so a voyage that never fans out
    /// behaves exactly as if lanes did not exist.
    /// </para>
    /// <para>
    /// A record belongs to the lanes that contain its mission, its commit, or its branch, tried in
    /// that order; a record that names none of them belongs to every lane. A record that belongs to
    /// more than one lane (an unstamped record armed at dispatch, or one measured at a planner commit
    /// above the fan-out) decides no lane of a fanned-out voyage: each lane is decided by records of
    /// its own.
    /// </para>
    /// </remarks>
    public static class VoyageLanes
    {
        #region Public-Methods

        /// <summary>
        /// Resolve the lanes of a voyage from its missions.
        /// </summary>
        /// <param name="missions">The voyage's missions, in any order. Null resolves to no lanes.</param>
        /// <returns>The lanes, ordered by the creation time of their leaf. A voyage that has not
        /// fanned out resolves to one lane holding every mission.</returns>
        public static IReadOnlyList<VoyageLane> Resolve(IEnumerable<Mission>? missions)
        {
            if (missions == null) return new List<VoyageLane>();
            List<Mission> list = missions
                .Where(mission => mission != null && !String.IsNullOrWhiteSpace(mission.Id))
                .GroupBy(mission => mission.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(mission => mission.CreatedUtc)
                .ThenBy(mission => mission.Id, StringComparer.Ordinal)
                .ToList();
            if (list.Count == 0) return new List<VoyageLane>();

            Dictionary<string, Mission> byId = list.ToDictionary(mission => mission.Id, StringComparer.Ordinal);

            // Group the missions into steps: a parallel stage group is one step.
            Dictionary<string, List<Mission>> steps = new Dictionary<string, List<Mission>>(StringComparer.Ordinal);
            Dictionary<string, string> stepOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Mission mission in list)
            {
                string key = StepKey(mission, byId);
                if (!steps.TryGetValue(key, out List<Mission>? members))
                {
                    members = new List<Mission>();
                    steps[key] = members;
                }
                members.Add(mission);
                stepOf[mission.Id] = key;
            }

            Dictionary<string, string?> parentStep = new Dictionary<string, string?>(StringComparer.Ordinal);
            Dictionary<string, int> childSteps = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<Mission>> step in steps)
            {
                string? parentId = ParentId(step.Value[0], byId);
                string? parent = parentId == null ? null : stepOf[parentId];
                if (parent != null && String.Equals(parent, step.Key, StringComparison.Ordinal)) parent = null;
                parentStep[step.Key] = parent;
                if (parent != null) childSteps[parent] = childSteps.TryGetValue(parent, out int children) ? children + 1 : 1;
            }

            // Only a step that more than one later step depends on splits the work. Without one the
            // voyage is a single line, including a voyage whose stages carry no dependency at all.
            if (!childSteps.Values.Any(children => children > 1)) return SingleLane(list);
            List<string> leaves = steps.Keys.Where(key => !childSteps.ContainsKey(key)).ToList();

            List<LanePath> paths = new List<LanePath>();
            foreach (string leaf in leaves)
            {
                List<string> path = new List<string>();
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                string? current = leaf;
                while (current != null && visited.Add(current))
                {
                    path.Add(current);
                    current = parentStep[current];
                }
                string leafMissionId = steps[leaf].OrderBy(m => m.CreatedUtc).ThenBy(m => m.Id, StringComparer.Ordinal).First().Id;
                paths.Add(new LanePath(leafMissionId, path));
            }

            Dictionary<string, int> stepUse = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LanePath lanePath in paths)
                foreach (string step in lanePath.Steps)
                    stepUse[step] = stepUse.TryGetValue(step, out int used) ? used + 1 : 1;

            List<VoyageLane> lanes = new List<VoyageLane>();
            foreach (LanePath lanePath in paths)
            {
                List<Mission> members = lanePath.Steps.SelectMany(step => steps[step]).ToList();
                List<Mission> own = lanePath.Steps.Where(step => stepUse[step] == 1).SelectMany(step => steps[step]).ToList();
                lanes.Add(new VoyageLane(lanePath.LeafMissionId, Ordered(members), Ordered(own)));
            }

            return lanes
                .OrderBy(lane => byId[lane.LeafMissionId].CreatedUtc)
                .ThenBy(lane => lane.LeafMissionId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Whether a voyage runs more than one line of work.
        /// </summary>
        /// <param name="lanes">The voyage's lanes from <see cref="Resolve"/>.</param>
        /// <returns>True when there is more than one lane.</returns>
        public static bool IsFanOut(IReadOnlyList<VoyageLane>? lanes)
        {
            return lanes != null && lanes.Count > 1;
        }

        /// <summary>
        /// The lane a mission belongs to, when it belongs to exactly one.
        /// </summary>
        /// <param name="lanes">The voyage's lanes.</param>
        /// <param name="mission">A mission of the voyage.</param>
        /// <returns>The mission's lane, or null when it belongs to none or to several (a planner stage
        /// above a fan-out).</returns>
        public static VoyageLane? LaneOfMission(IReadOnlyList<VoyageLane>? lanes, Mission? mission)
        {
            if (lanes == null || mission == null) return null;
            List<VoyageLane> containing = lanes.Where(lane => lane.Contains(mission.Id)).ToList();
            return containing.Count == 1 ? containing[0] : null;
        }

        /// <summary>
        /// The lanes a Check record measures.
        /// </summary>
        /// <param name="lanes">The voyage's lanes.</param>
        /// <param name="run">A Check record attached to the voyage or to one of its missions.</param>
        /// <returns>The lanes that contain the record's mission, else its commit, else its branch; every
        /// lane when the record names none of them.</returns>
        public static IReadOnlyList<VoyageLane> LanesOfRecord(IReadOnlyList<VoyageLane>? lanes, CheckRun? run)
        {
            if (lanes == null || lanes.Count == 0 || run == null) return new List<VoyageLane>();
            if (lanes.Count == 1) return lanes;

            if (!String.IsNullOrWhiteSpace(run.MissionId))
            {
                List<VoyageLane> byMission = lanes.Where(lane => lane.Contains(run.MissionId)).ToList();
                if (byMission.Count > 0) return byMission;
            }

            if (!String.IsNullOrWhiteSpace(run.CommitHash))
            {
                List<VoyageLane> byCommit = lanes
                    .Where(lane => lane.Missions.Any(mission => CheckRunGateRules.SameCommit(mission.CommitHash, run.CommitHash)))
                    .ToList();
                if (byCommit.Count > 0) return byCommit;
            }

            if (!String.IsNullOrWhiteSpace(run.BranchName))
            {
                List<VoyageLane> byBranch = lanes
                    .Where(lane => lane.Missions.Any(mission => String.Equals(mission.BranchName, run.BranchName, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (byBranch.Count > 0) return byBranch;
            }

            return lanes;
        }

        /// <summary>
        /// The single lane a Check record measures, when it measures exactly one.
        /// </summary>
        /// <param name="lanes">The voyage's lanes.</param>
        /// <param name="run">A Check record.</param>
        /// <returns>The record's lane, or null when it belongs to none or to several.</returns>
        public static VoyageLane? SingleLaneOfRecord(IReadOnlyList<VoyageLane>? lanes, CheckRun? run)
        {
            IReadOnlyList<VoyageLane> member = LanesOfRecord(lanes, run);
            return member.Count == 1 ? member[0] : null;
        }

        /// <summary>
        /// The records that decide a Judge's check gate: every record attached to the Judge itself, and
        /// every record that measures the Judge's lane and no other.
        /// </summary>
        /// <remarks>
        /// On a voyage that has not fanned out every record measures the one lane, so the records come
        /// back unchanged. On a fanned-out voyage a record of another lane neither holds nor rejects
        /// this Judge, and a record shared by several lanes decides none of them.
        /// </remarks>
        /// <param name="judge">The Judge mission.</param>
        /// <param name="missions">The voyage's missions.</param>
        /// <param name="checks">The records collected for the Judge.</param>
        /// <returns>The records in the Judge's lane.</returns>
        public static List<CheckRun> ChecksForLaneOf(Mission judge, IEnumerable<Mission>? missions, IEnumerable<CheckRun>? checks)
        {
            if (judge == null) throw new ArgumentNullException(nameof(judge));
            List<CheckRun> list = checks?.Where(run => run != null).ToList() ?? new List<CheckRun>();
            IReadOnlyList<VoyageLane> lanes = Resolve(missions);
            if (!IsFanOut(lanes)) return list;

            VoyageLane? lane = LaneOfMission(lanes, judge);
            if (lane == null) return list;

            return list
                .Where(run => String.Equals(run.MissionId, judge.Id, StringComparison.Ordinal)
                    || ReferenceEquals(SingleLaneOfRecord(lanes, run), lane))
                .ToList();
        }

        #endregion

        #region Private-Methods

        private static string? ParentId(Mission mission, Dictionary<string, Mission> byId)
        {
            string? parent = mission.DependsOnMissionId;
            if (String.IsNullOrWhiteSpace(parent)) return null;
            if (String.Equals(parent, mission.Id, StringComparison.Ordinal)) return null;
            return byId.ContainsKey(parent) ? parent : null;
        }

        private static string StepKey(Mission mission, Dictionary<string, Mission> byId)
        {
            const char separator = '\u001F';
            string? parent = ParentId(mission, byId);
            // Only stages that share a parent can form a parallel group. Stages with no parent in the
            // voyage are separate roots, such as the first stages of separately dispatched chains.
            if (mission.StageOrder.HasValue && parent != null)
                return "group" + separator + parent + separator + mission.StageOrder.Value;
            return "mission" + separator + mission.Id;
        }

        private static List<VoyageLane> SingleLane(List<Mission> list)
        {
            Mission leaf = list[list.Count - 1];
            return new List<VoyageLane> { new VoyageLane(leaf.Id, list, list) };
        }

        private static List<Mission> Ordered(IEnumerable<Mission> missions)
        {
            return missions
                .OrderBy(mission => mission.CreatedUtc)
                .ThenBy(mission => mission.Id, StringComparer.Ordinal)
                .ToList();
        }

        #endregion

        #region Private-Classes

        private sealed class LanePath
        {
            public LanePath(string leafMissionId, List<string> steps)
            {
                LeafMissionId = leafMissionId;
                Steps = steps;
            }

            public string LeafMissionId { get; }

            public List<string> Steps { get; }
        }

        #endregion
    }
}
