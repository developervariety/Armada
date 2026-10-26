namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private static void ValidateBarrierSliceTitlesBeforeRepair(
            List<Mission> rows, List<Mission?> roots, List<ParsedArchitectMission> parsed)
        {
            if (parsed.Count == 0 || !parsed[parsed.Count - 1].WaitForOtherMissions) return;
            for (int index = 0; index < roots.Count; index++)
            {
                Mission? root = roots[index];
                if (root == null) continue;
                string expectedTitle = parsed[index].Title;
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                Mission current = root;
                while (true)
                {
                    if (!visited.Add(current.Id))
                        throw new ArchitectRecoveryUnsafeException("a slice chain contains a cycle before repair");
                    List<Mission> children = ArchitectTemplateChildren(rows, current);
                    if (children.Count > 1)
                        throw new ArchitectRecoveryUnsafeException("a slice has multiple pipeline children before repair");
                    if (children.Count == 0) break;
                    Mission child = children[0];
                    string expectedStageTitle = expectedTitle + " [" + (child.Persona ?? "Worker") + "]";
                    if (!String.Equals(child.Title, expectedStageTitle, StringComparison.OrdinalIgnoreCase))
                        throw new ArchitectRecoveryUnsafeException("a slice has an unexpected pipeline child title before repair");
                    current = child;
                }
            }
        }
    }
}
