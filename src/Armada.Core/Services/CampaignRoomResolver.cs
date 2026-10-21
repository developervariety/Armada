namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for which campaign an objective belongs to, and which coordination room
    /// that campaign uses.
    /// </summary>
    /// <remarks>
    /// A campaign is an objective tree. The nearest ancestor (the objective itself included) that
    /// carries a <c>campaign:&lt;name&gt;</c> tag is the campaign root. The campaign's room key is
    /// <c>campaign-&lt;name&gt;</c>, reduced to lowercase letters, digits, dots, dashes and
    /// underscores so it is safe in a URL path.
    /// </remarks>
    public static class CampaignRoomResolver
    {
        #region Public-Members

        /// <summary>Tag prefix that marks a campaign root.</summary>
        public const string TagPrefix = "campaign:";

        /// <summary>Prefix of every campaign room key.</summary>
        public const string RoomKeyPrefix = "campaign-";

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

        /// <summary>
        /// The room key for a campaign name, or null when the name reduces to nothing.
        /// </summary>
        /// <param name="campaignName">Campaign name from a <c>campaign:</c> tag.</param>
        /// <returns>The room key, or null.</returns>
        public static string? BuildRoomKey(string? campaignName)
        {
            if (String.IsNullOrWhiteSpace(campaignName)) return null;
            StringBuilder sb = new StringBuilder();
            bool lastWasDash = false;
            foreach (char c in campaignName.Trim().ToLowerInvariant())
            {
                bool safe = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_';
                if (safe)
                {
                    sb.Append(c);
                    lastWasDash = false;
                }
                else if (!lastWasDash)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }

            string slug = sb.ToString().Trim('-');
            return slug.Length == 0 ? null : RoomKeyPrefix + slug;
        }

        /// <summary>
        /// True when a room key names a campaign room.
        /// </summary>
        /// <param name="roomKey">Room key.</param>
        /// <returns>True for a campaign room key.</returns>
        public static bool IsCampaignRoomKey(string? roomKey)
        {
            return !String.IsNullOrEmpty(roomKey)
                && roomKey.StartsWith(RoomKeyPrefix, StringComparison.Ordinal)
                && roomKey.Length > RoomKeyPrefix.Length;
        }

        /// <summary>
        /// The campaign room key for an objective, or null when the objective is not in a campaign.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="objectiveId">Objective id; null or empty returns null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The campaign room key, or null.</returns>
        public static async Task<string?> ResolveRoomKeyForObjectiveAsync(DatabaseDriver database, string? objectiveId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(objectiveId)) return null;

            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            string? currentId = objectiveId;
            while (!String.IsNullOrWhiteSpace(currentId) && visited.Add(currentId!))
            {
                Objective? current = await database.Objectives.ReadAsync(currentId!, token).ConfigureAwait(false);
                if (current == null) return null;
                string? name = ReadCampaignName(current);
                if (name != null) return BuildRoomKey(name);
                if (current.Tags != null && current.Tags.Any(tag => tag != null && tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)))
                    return null;
                currentId = current.ParentObjectiveId;
            }

            return null;
        }

        /// <summary>
        /// The campaign room key for a voyage: the campaign of the objective the voyage serves
        /// (see <see cref="VoyageObjectiveResolver"/>), or null when that objective is not in a campaign.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage id; null or empty returns null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The campaign room key, or null.</returns>
        public static async Task<string?> ResolveRoomKeyForVoyageAsync(DatabaseDriver database, string? voyageId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(voyageId)) return null;

            Voyage? voyage = await database.Voyages.ReadAsync(voyageId!, token).ConfigureAwait(false);
            List<Objective> objectives = await database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            Objective? served = VoyageObjectiveResolver.Choose(
                objectives, voyageId!, VoyageObjectiveResolver.ReadBriefObjectiveId(voyage?.Description));
            if (served == null) return null;

            Dictionary<string, Objective> byId = new Dictionary<string, Objective>(StringComparer.Ordinal);
            foreach (Objective objective in objectives)
            {
                if (!String.IsNullOrEmpty(objective.Id)) byId[objective.Id] = objective;
            }

            Objective? root = FindCampaignRoot(served, byId);
            return root == null ? null : BuildRoomKey(ReadCampaignName(root));
        }

        #endregion
    }
}
