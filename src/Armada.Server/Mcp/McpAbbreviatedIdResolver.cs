namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Resolves an abbreviated mission, voyage or objective id to the one full id the caller can see. An
    /// abbreviation is the prefix and creation-time part of a full id (<c>msn_</c> plus eight base-36
    /// characters); the time part is the record's creation time in milliseconds, so candidates are read by
    /// creation time and matched by prefix. A record the caller cannot see is never a candidate, so an
    /// abbreviation reveals nothing the full id would not. Two visible candidates make the abbreviation
    /// ambiguous, and nothing is read.
    /// </summary>
    internal static class McpAbbreviatedIdResolver
    {
        #region Public-Members

        /// <summary>
        /// Denial reason when an abbreviated id names more than one record the caller can see.
        /// </summary>
        public const string AmbiguousReason = "ambiguous_id";

        #endregion

        #region Private-Members

        private const int _TimePartLength = 8;

        private static readonly TimeSpan _CreationWindow = TimeSpan.FromSeconds(5);

        private static readonly Regex _Abbreviated = new Regex(
            @"^(?:msn|vyg|obj)_[0-9a-z]{8}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>True when the id is an abbreviation, such as <c>msn_mabc1234</c>.</summary>
        /// <param name="id">Record id.</param>
        /// <returns>True for an abbreviated id.</returns>
        public static bool IsAbbreviated(string? id)
        {
            return !String.IsNullOrEmpty(id) && _Abbreviated.IsMatch(id);
        }

        /// <summary>Resolve a mission id that may be abbreviated.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Mission id as given.</param>
        /// <returns>The id to read.</returns>
        public static async Task<McpAbbreviatedIdResolution> ResolveMissionAsync(DatabaseDriver database, AuthContext caller, string? id)
        {
            if (!TryReadCreationWindow(id, out EnumerationQuery query)) return AsGiven(id);
            EnumerationResult<Mission> page = await database.Missions.EnumerateAsync(query).ConfigureAwait(false);
            List<string> visible = new List<string>();
            foreach (Mission mission in page.Objects.Where(mission => Expands(id!, mission.Id)))
            {
                bool canSee = !String.IsNullOrEmpty(caller.MissionId)
                    ? McpMissionEvidenceScope.SameOwner(caller, mission.TenantId, mission.UserId)
                    : await CallerScopedRead.ReadMissionAsync(database, caller, mission.Id).ConfigureAwait(false) != null;
                if (canSee) visible.Add(mission.Id);
            }
            return Settle(id!, visible);
        }

        /// <summary>Resolve a voyage id that may be abbreviated.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Voyage id as given.</param>
        /// <returns>The id to read.</returns>
        public static async Task<McpAbbreviatedIdResolution> ResolveVoyageAsync(DatabaseDriver database, AuthContext caller, string? id)
        {
            if (!TryReadCreationWindow(id, out EnumerationQuery query)) return AsGiven(id);
            EnumerationResult<Voyage> page = await database.Voyages.EnumerateAsync(query).ConfigureAwait(false);
            List<string> visible = new List<string>();
            foreach (Voyage voyage in page.Objects.Where(voyage => Expands(id!, voyage.Id)))
            {
                bool canSee = !String.IsNullOrEmpty(caller.MissionId)
                    ? McpMissionEvidenceScope.SameOwner(caller, voyage.TenantId, voyage.UserId)
                    : await CallerScopedRead.ReadVoyageAsync(database, caller, voyage.Id).ConfigureAwait(false) != null;
                if (canSee) visible.Add(voyage.Id);
            }
            return Settle(id!, visible);
        }

        /// <summary>Resolve an objective id that may be abbreviated.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Objective id as given.</param>
        /// <returns>The id to read.</returns>
        public static async Task<McpAbbreviatedIdResolution> ResolveObjectiveAsync(DatabaseDriver database, AuthContext caller, string? id)
        {
            if (!IsAbbreviated(id) || !id!.StartsWith("obj_", StringComparison.Ordinal)) return AsGiven(id);
            List<Objective> objectives = await database.Objectives.EnumerateAsync().ConfigureAwait(false);
            List<string> visible = new List<string>();
            foreach (Objective objective in objectives.Where(objective => Expands(id, objective.Id)))
            {
                bool canSee = !String.IsNullOrEmpty(caller.MissionId)
                    ? McpMissionEvidenceScope.SameOwner(caller, objective.TenantId, objective.UserId)
                    : await CallerScopedRead.ReadObjectiveAsync(database, caller, objective.Id).ConfigureAwait(false) != null;
                if (canSee) visible.Add(objective.Id);
            }
            return Settle(id, visible);
        }

        /// <summary>
        /// Resolve an abbreviated id against records the caller already holds, such as its owner's objectives.
        /// </summary>
        /// <param name="id">Id as given.</param>
        /// <param name="candidates">Full ids the caller can see.</param>
        /// <returns>The id to read.</returns>
        public static McpAbbreviatedIdResolution ResolveAmong(string? id, IEnumerable<string> candidates)
        {
            if (!IsAbbreviated(id)) return AsGiven(id);
            return Settle(id!, candidates.Where(candidate => Expands(id!, candidate)).Distinct(StringComparer.Ordinal).ToList());
        }

        #endregion

        #region Private-Methods

        private static bool Expands(string abbreviated, string? fullId)
        {
            return !String.IsNullOrEmpty(fullId) && fullId.StartsWith(abbreviated + "_", StringComparison.Ordinal);
        }

        private static McpAbbreviatedIdResolution AsGiven(string? id)
        {
            return new McpAbbreviatedIdResolution { Id = id ?? String.Empty };
        }

        private static McpAbbreviatedIdResolution Settle(string id, List<string> visible)
        {
            if (visible.Count == 1) return new McpAbbreviatedIdResolution { Id = visible[0] };
            return new McpAbbreviatedIdResolution { Id = id, Ambiguous = visible.Count > 1 };
        }

        private static bool TryReadCreationWindow(string? id, out EnumerationQuery query)
        {
            query = new EnumerationQuery();
            if (!IsAbbreviated(id)) return false;
            string timePart = id!.Substring(id.Length - _TimePartLength);
            long milliseconds = 0;
            foreach (char c in timePart)
            {
                int digit = c >= '0' && c <= '9' ? c - '0' : c - 'a' + 10;
                milliseconds = milliseconds * 36 + digit;
            }
            // Eight base-36 digits stay below 36^8 milliseconds, so the time is always a valid date.
            DateTime created = DateTime.UnixEpoch.AddMilliseconds(milliseconds);
            query.CreatedAfter = created - _CreationWindow;
            query.CreatedBefore = created + _CreationWindow;
            query.PageSize = 1000;
            return true;
        }

        #endregion
    }
}
