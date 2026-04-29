namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Models;

    /// <summary>
    /// Validates and topologically sorts a list of mission descriptions
    /// that use logical alias references for inter-mission dependencies.
    /// Pure logic with no I/O side effects.
    /// </summary>
    public static class MissionAliasResolver
    {
        /// <summary>
        /// Validates alias uniqueness and dependency references, then returns
        /// the input list reordered so that each mission appears after every
        /// mission it depends on via
        /// <see cref="MissionDescription.DependsOnMissionAlias"/>.
        /// If no missions use aliases, the original list is returned unchanged.
        /// </summary>
        /// <param name="input">Mission descriptions to validate and order.</param>
        /// <returns>
        /// Mission descriptions in topological creation order.
        /// Each item retains its <see cref="MissionDescription.DependsOnMissionAlias"/>
        /// value; the caller is responsible for resolving it to a concrete
        /// <c>msn_*</c> ID after creating the dependency mission.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="input"/> is null.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// Thrown when a duplicate alias is detected, a referenced alias is
        /// unknown, or the alias dependency graph contains a cycle.
        /// </exception>
        public static IReadOnlyList<MissionDescription> ResolveAndOrder(IReadOnlyList<MissionDescription> input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Fast path: no aliases present -- return input unchanged to preserve
            // original ordering and avoid unnecessary allocations.
            bool hasAnyAlias = false;
            foreach (MissionDescription md in input)
            {
                if (!String.IsNullOrEmpty(md.Alias) || !String.IsNullOrEmpty(md.DependsOnMissionAlias))
                {
                    hasAnyAlias = true;
                    break;
                }
            }
            if (!hasAnyAlias) return input;

            int count = input.Count;

            // Validate alias uniqueness; build alias -> index map.
            Dictionary<string, int> aliasIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string? alias = input[i].Alias;
                if (String.IsNullOrEmpty(alias)) continue;
                if (aliasIndex.ContainsKey(alias))
                    throw new InvalidDataException("Duplicate alias '" + alias + "' at mission index " + i);
                aliasIndex[alias] = i;
            }

            // Validate every DependsOnMissionAlias references a declared alias.
            for (int i = 0; i < count; i++)
            {
                string? dep = input[i].DependsOnMissionAlias;
                if (String.IsNullOrEmpty(dep)) continue;
                if (!aliasIndex.ContainsKey(dep))
                    throw new InvalidDataException("Mission at index " + i + " references unknown alias '" + dep + "'");
            }

            // Build adjacency structure for Kahn's topological sort.
            // successors[i] contains all mission indices that depend on mission i.
            int[] inDegree = new int[count];
            List<int>[] successors = new List<int>[count];
            for (int i = 0; i < count; i++)
                successors[i] = new List<int>();

            for (int i = 0; i < count; i++)
            {
                string? dep = input[i].DependsOnMissionAlias;
                if (String.IsNullOrEmpty(dep)) continue;
                int depIdx = aliasIndex[dep];
                successors[depIdx].Add(i);
                inDegree[i]++;
            }

            // Kahn's algorithm: start from all roots (in-degree == 0).
            Queue<int> queue = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                if (inDegree[i] == 0) queue.Enqueue(i);
            }

            List<MissionDescription> result = new List<MissionDescription>(count);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                result.Add(input[idx]);
                foreach (int successor in successors[idx])
                {
                    inDegree[successor]--;
                    if (inDegree[successor] == 0)
                        queue.Enqueue(successor);
                }
            }

            if (result.Count != count)
                throw new InvalidDataException("Cycle detected in mission alias dependency graph");

            return result;
        }
    }
}
