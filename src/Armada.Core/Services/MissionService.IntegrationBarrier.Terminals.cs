namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private static List<Mission> GetArchitectBarrierTerminals(List<Mission> rows, List<Mission> roots)
        {
            List<Mission> terminals = new List<Mission>();
            if (roots.Count < 2) return terminals;
            HashSet<string> rootIds = new HashSet<string>(roots.Select(root => root.Id), StringComparer.Ordinal);
            for (int index = 0; index < roots.Count - 1; index++)
            {
                Mission root = roots[index];
                Mission current = root;
                string suffix = " [Worker]";
                if (!root.Title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    throw new ArchitectRecoveryUnsafeException("a slice root has an invalid Worker title");
                string branchTitle = root.Title.Substring(0, root.Title.Length - suffix.Length);
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                while (visited.Add(current.Id))
                {
                    List<Mission> children = rows.Where(item => item.DependsOnMissionId == current.Id
                            && !IsChainBoundaryDependent(item)
                            && (!rootIds.Contains(item.Id) || item.Id == root.Id)
                            && String.Equals(item.Title, branchTitle + " [" + (item.Persona ?? "Worker") + "]", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => item.CreatedUtc)
                        .ToList();
                    if (children.Count > 1)
                        throw new ArchitectRecoveryUnsafeException("a slice has multiple pipeline children with the same stage title");
                    if (children.Count == 0) break;
                    current = children[0];
                }
                terminals.Add(current);
            }
            return terminals;
        }

        private static string ComputeArchitectPlanHash(IReadOnlyList<ParsedArchitectMission> parsed)
        {
            string canonical = JsonSerializer.Serialize(parsed.Select(item => new
            {
                item.Title,
                item.Description,
                item.DependsOnReference,
                item.WaitForOtherMissions
            }));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

    }
}
