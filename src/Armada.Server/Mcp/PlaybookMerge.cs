namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Shared playbook merge logic used across all MCP dispatch paths
    /// (voyage, architect, and standalone mission creation).
    /// </summary>
    public static class PlaybookMerge
    {
        /// <summary>
        /// Merges vessel default playbooks with caller-supplied entries.
        /// Defaults populate first; if a caller entry shares a playbookId with a default,
        /// the caller's deliveryMode replaces the default's value. Remaining caller entries
        /// that have no matching default are appended at the end.
        /// Comparison is case-sensitive on playbookId.
        /// </summary>
        /// <param name="vesselDefaults">Vessel default playbooks (may be null or empty).</param>
        /// <param name="callerEntries">Caller-supplied playbooks (may be empty).</param>
        /// <returns>Merged playbook list with defaults first, caller overrides applied.</returns>
        public static List<SelectedPlaybook> MergeWithVesselDefaults(
            IReadOnlyList<SelectedPlaybook>? vesselDefaults,
            IReadOnlyList<SelectedPlaybook> callerEntries)
        {
            List<SelectedPlaybook> merged = new List<SelectedPlaybook>();
            if (vesselDefaults != null)
            {
                foreach (SelectedPlaybook d in vesselDefaults)
                    merged.Add(new SelectedPlaybook { PlaybookId = d.PlaybookId, DeliveryMode = d.DeliveryMode });
            }
            foreach (SelectedPlaybook caller in callerEntries)
            {
                SelectedPlaybook? existing = merged.FirstOrDefault(m => m.PlaybookId == caller.PlaybookId);
                if (existing != null)
                    existing.DeliveryMode = caller.DeliveryMode;
                else
                    merged.Add(new SelectedPlaybook { PlaybookId = caller.PlaybookId, DeliveryMode = caller.DeliveryMode });
            }
            return merged;
        }
    }
}
