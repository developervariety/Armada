namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for which campaign an objective belongs to.
    /// </summary>
    /// <remarks>
    /// A campaign is an objective tree. The nearest ancestor (the objective itself included) that
    /// carries a <c>campaign:&lt;name&gt;</c> tag is the campaign root.
    /// </remarks>
    public static class CampaignRoomResolver
    {
        #region Public-Members

        /// <summary>Tag prefix that marks a campaign root.</summary>
        public const string TagPrefix = "campaign:";

        #endregion

        #region Public-Methods

        /// <summary>
        /// The campaign name an objective's own tags declare, or null when it carries no
        /// non-empty <c>campaign:</c> tag.
        /// </summary>
        /// <param name="objective">Objective.</param>
        /// <returns>The campaign name, or null.</returns>
        public static string? ReadCampaignName(Objective? objective)
        {
            if (objective?.Tags == null) return null;
            foreach (string tag in objective.Tags)
            {
                if (tag == null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string name = tag.Substring(TagPrefix.Length).Trim();
                if (name.Length > 0) return name;
            }

            return null;
        }

        /// <summary>
        /// The campaign root of an objective: the nearest ancestor, the objective itself included,
        /// that carries a <c>campaign:</c> tag. Returns null when no ancestor is tagged, when a
        /// parent is not in the lookup, or when the parent chain loops.
        /// </summary>
        /// <param name="objective">Objective to start from.</param>
        /// <param name="byId">Lookup of objectives by id that holds the ancestors.</param>
        /// <returns>The campaign root, or null.</returns>
        public static Objective? FindCampaignRoot(Objective objective, IReadOnlyDictionary<string, Objective> byId)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            if (byId == null) throw new ArgumentNullException(nameof(byId));

            Objective current = objective;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!visited.Add(current.Id)) return null;
                if (current.Tags != null && current.Tags.Any(tag => tag != null && tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)))
                    return current;
                if (String.IsNullOrWhiteSpace(current.ParentObjectiveId)) return null;
                if (!byId.TryGetValue(current.ParentObjectiveId!, out Objective? parent)) return null;
                current = parent;
            }
        }

        #endregion
    }
}
