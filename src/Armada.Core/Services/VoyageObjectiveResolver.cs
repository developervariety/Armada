namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for which objective a voyage serves.
    /// </summary>
    /// <remarks>
    /// An umbrella objective and its child objectives can all list the same voyage, so "the first
    /// objective that lists the voyage" can return the umbrella, and a stage is then judged against the
    /// umbrella's acceptance criteria instead of the child's. The voyage brief names the objective it was
    /// rendered from, so that objective wins. Without a brief marker, the most specific listing
    /// objective wins: one that is the parent of another listing objective is passed over, and the
    /// newest of the rest is chosen.
    /// </remarks>
    public static class VoyageObjectiveResolver
    {
        #region Private-Members

        private static readonly Regex _BriefMarker = new Regex(
            @"<!-- armada-objective-brief:(?<id>obj_[A-Za-z0-9_]+) -->",
            RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// The objective id named by the objective-brief marker in a voyage description, or null.
        /// </summary>
        /// <param name="voyageDescription">Voyage description.</param>
        /// <returns>The objective id, or null when the description carries no marker.</returns>
        public static string? ReadBriefObjectiveId(string? voyageDescription)
        {
            if (String.IsNullOrEmpty(voyageDescription)) return null;
            Match match = _BriefMarker.Match(voyageDescription);
            return match.Success ? match.Groups["id"].Value : null;
        }

        /// <summary>
        /// Choose the objective a voyage serves from a set of objectives.
        /// </summary>
        /// <param name="objectives">Candidate objectives.</param>
        /// <param name="voyageId">Voyage id.</param>
        /// <param name="briefObjectiveId">Objective id from the voyage brief marker, or null.</param>
        /// <returns>The objective, or null when none lists the voyage and the marker names none.</returns>
        public static Objective? Choose(IEnumerable<Objective?> objectives, string voyageId, string? briefObjectiveId)
        {
            if (objectives == null) throw new ArgumentNullException(nameof(objectives));
            List<Objective> all = objectives.Where(item => item != null).Select(item => item!).ToList();

            if (!String.IsNullOrEmpty(briefObjectiveId))
            {
                Objective? named = all.FirstOrDefault(item => String.Equals(item.Id, briefObjectiveId, StringComparison.Ordinal));
                if (named != null) return named;
            }

            if (String.IsNullOrEmpty(voyageId)) return null;
            List<Objective> listing = all
                .Where(item => item.VoyageIds != null && item.VoyageIds.Contains(voyageId, StringComparer.Ordinal))
                .ToList();
            if (listing.Count <= 1) return listing.FirstOrDefault();

            List<Objective> specific = listing
                .Where(candidate => !listing.Any(other => String.Equals(other.ParentObjectiveId, candidate.Id, StringComparison.Ordinal)))
                .ToList();
            return (specific.Count > 0 ? specific : listing)
                .OrderByDescending(item => item.CreatedUtc)
                .First();
        }

        /// <summary>
        /// Resolve the objective a voyage serves.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage id; null or empty returns null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The objective, or null.</returns>
        public static async Task<Objective?> ResolveAsync(DatabaseDriver database, string? voyageId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(voyageId)) return null;

            Voyage? voyage = await database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            List<Objective> objectives = await database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            return Choose(objectives, voyageId, ReadBriefObjectiveId(voyage?.Description));
        }

        #endregion
    }
}
