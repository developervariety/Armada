namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Shared playbook merge logic used across dispatch paths (voyage, architect,
    /// standalone missions, admiral voyage expansion).
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
        /// <param name="vesselDefaults">Default playbooks (may be null or empty). For voyage propagation this is commonly the voyage-level merged selections.</param>
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
                {
                    merged.Add(new SelectedPlaybook { PlaybookId = d.PlaybookId, DeliveryMode = d.DeliveryMode });
                }
            }

            foreach (SelectedPlaybook caller in callerEntries)
            {
                SelectedPlaybook? existing = merged.FirstOrDefault(m => m.PlaybookId == caller.PlaybookId);
                if (existing != null)
                {
                    existing.DeliveryMode = caller.DeliveryMode;
                }
                else
                {
                    merged.Add(new SelectedPlaybook { PlaybookId = caller.PlaybookId, DeliveryMode = caller.DeliveryMode });
                }
            }

            return merged;
        }
    }
}
