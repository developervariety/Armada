namespace Armada.Core.Authorization
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// The one resolver for abbreviated record ids. An abbreviation is the prefix and creation-time part of a
    /// full id (<c>msn_</c> plus eight base-36 characters); the time part is the record's creation time in
    /// milliseconds, so candidates are read by creation time and matched by prefix. Missions, voyages,
    /// objectives, check runs, incidents, merge entries, signals, captains, vessels, fleets, docks, releases
    /// and deployments resolve. A candidate the caller cannot see is never counted, so
    /// an abbreviation reveals nothing the full id would not: no visible match leaves the id as given, which
    /// then reads as absent, and two visible matches make it ambiguous. Every read and write that accepts a
    /// record id resolves it here before its normal scope check, and a write stores the full id.
    /// </summary>
    public static class AbbreviatedIdResolver
    {
        #region Public-Members

        /// <summary>
        /// Reason when an abbreviated id names more than one record the caller can see.
        /// </summary>
        public const string AmbiguousReason = "ambiguous_id";

        #endregion

        #region Private-Members

        private const int _TimePartLength = 8;

        private const int _MaxCandidates = 1000;

        private static readonly TimeSpan _CreationWindow = TimeSpan.FromSeconds(5);

        private static readonly Regex _Abbreviated = new Regex(
            @"^(?:msn|vyg|obj|chk|inc|mrg|sig|cpt|vsl|flt|dck|rel|dpl)_[0-9a-z]{8}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>True when the id is an abbreviation, such as <c>msn_mabc1234</c>.</summary>
        /// <param name="id">Record id.</param>
        /// <returns>True for an abbreviated id.</returns>
        public static bool IsAbbreviated(string? id)
        {
            return !String.IsNullOrEmpty(id) && _Abbreviated.IsMatch(id.Trim());
        }

        /// <summary>
        /// Resolve an id that may be abbreviated, counting only the candidates <paramref name="canSee"/> accepts.
        /// Pass the same scoped read the caller's request is checked with, so the abbreviation sees exactly what
        /// the full id would.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="id">Id as given.</param>
        /// <param name="canSee">True when the caller can see the record with this full id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The id to use.</returns>
        public static async Task<AbbreviatedIdResolution> ResolveAsync(
            DatabaseDriver database,
            string? id,
            Func<string, Task<bool>> canSee,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (canSee == null) throw new ArgumentNullException(nameof(canSee));
            if (!IsAbbreviated(id)) return AsGiven(id);
            string abbreviated = id!.Trim();

            List<string> visible = new List<string>();
            foreach (string candidate in await ReadCandidateIdsAsync(database, abbreviated, token).ConfigureAwait(false))
            {
                if (await canSee(candidate).ConfigureAwait(false)) visible.Add(candidate);
            }
            return Settle(abbreviated, visible);
        }

        /// <summary>
        /// Resolve an id that may be abbreviated against what the caller can see: a mission caller sees its own
        /// owner's records, and any other caller sees its <see cref="CallerScopedRead"/> scope.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="id">Id as given.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The id to use.</returns>
        public static async Task<AbbreviatedIdResolution> ResolveForCallerAsync(
            DatabaseDriver database,
            AuthContext caller,
            string? id,
            CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            return await ResolveAsync(database, id, candidate => CallerCanSeeAsync(database, caller, candidate, token), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve an abbreviated id against records the caller already holds, such as its owner's objectives.
        /// </summary>
        /// <param name="id">Id as given.</param>
        /// <param name="candidates">Full ids the caller can see.</param>
        /// <returns>The id to use.</returns>
        public static AbbreviatedIdResolution ResolveAmong(string? id, IEnumerable<string> candidates)
        {
            if (!IsAbbreviated(id)) return AsGiven(id);
            string abbreviated = id!.Trim();
            return Settle(abbreviated, candidates.Where(candidate => Expands(abbreviated, candidate)).Distinct(StringComparer.Ordinal).ToList());
        }

        /// <summary>
        /// Text naming an ambiguous abbreviation, led by <see cref="AmbiguousReason"/> so a caller can match it.
        /// </summary>
        /// <param name="label">What the id names, such as "Parent objective".</param>
        /// <param name="id">Abbreviated id as given.</param>
        /// <returns>Refusal text.</returns>
        public static string DescribeAmbiguous(string label, string id)
        {
            return AmbiguousReason + ": " + label + " id " + id + " matches more than one record visible to this caller. Pass the full id.";
        }

        #endregion

        #region Private-Methods

        private static bool Expands(string abbreviated, string? fullId)
        {
            return !String.IsNullOrEmpty(fullId) && fullId.StartsWith(abbreviated + "_", StringComparison.Ordinal);
        }

        private static AbbreviatedIdResolution AsGiven(string? id)
        {
            return new AbbreviatedIdResolution { Id = id ?? String.Empty };
        }

        private static AbbreviatedIdResolution Settle(string id, List<string> visible)
        {
            if (visible.Count == 1) return new AbbreviatedIdResolution { Id = visible[0], Resolved = true };
            bool ambiguous = visible.Count > 1;
            return new AbbreviatedIdResolution
            {
                Id = id,
                Ambiguous = ambiguous,
                Candidates = ambiguous ? visible.OrderBy(candidate => candidate, StringComparer.Ordinal).ToList() : new List<string>()
            };
        }

        private static DateTime ReadCreationTime(string abbreviated)
        {
            string timePart = abbreviated.Substring(abbreviated.Length - _TimePartLength);
            long milliseconds = 0;
            foreach (char c in timePart)
            {
                int digit = c >= '0' && c <= '9' ? c - '0' : c - 'a' + 10;
                milliseconds = milliseconds * 36 + digit;
            }
            // Eight base-36 digits stay below 36^8 milliseconds, so the time is always a valid date.
            return DateTime.UnixEpoch.AddMilliseconds(milliseconds);
        }

        private static async Task<List<string>> ReadCandidateIdsAsync(DatabaseDriver database, string abbreviated, CancellationToken token)
        {
            DateTime created = ReadCreationTime(abbreviated);
            DateTime after = created - _CreationWindow;
            DateTime before = created + _CreationWindow;
            IEnumerable<string> ids;
            switch (abbreviated.Substring(0, 4))
            {
                case "msn_":
                    ids = (await database.Missions.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(mission => mission.Id);
                    break;
                case "vyg_":
                    ids = (await database.Voyages.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(voyage => voyage.Id);
                    break;
                case "obj_":
                    ids = (await database.Objectives.EnumerateAsync(token).ConfigureAwait(false)).Select(objective => objective.Id);
                    break;
                case "chk_":
                    ids = (await database.CheckRuns.EnumerateAsync(new CheckRunQuery { FromUtc = after, ToUtc = before, PageSize = _MaxCandidates }, token).ConfigureAwait(false))
                        .Objects.Select(run => run.Id);
                    break;
                case "mrg_":
                    ids = (await database.MergeEntries.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(entry => entry.Id);
                    break;
                case "sig_":
                    ids = (await database.Signals.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(signal => signal.Id);
                    break;
                case "cpt_":
                    ids = (await database.Captains.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(captain => captain.Id);
                    break;
                case "vsl_":
                    ids = (await database.Vessels.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(vessel => vessel.Id);
                    break;
                case "flt_":
                    ids = (await database.Fleets.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(fleet => fleet.Id);
                    break;
                case "dck_":
                    ids = (await database.Docks.EnumerateAsync(Window(after, before), token).ConfigureAwait(false)).Objects.Select(dock => dock.Id);
                    break;
                case "rel_":
                    ids = (await database.Releases.EnumerateAsync(new ReleaseQuery { FromUtc = after, ToUtc = before, PageSize = _MaxCandidates }, token).ConfigureAwait(false))
                        .Objects.Select(release => release.Id);
                    break;
                case "dpl_":
                    ids = (await database.Deployments.EnumerateAsync(new DeploymentQuery { FromUtc = after, ToUtc = before, PageSize = _MaxCandidates }, token).ConfigureAwait(false))
                        .Objects.Select(deployment => deployment.Id);
                    break;
                default:
                    // An incident is a series of snapshot events; its first snapshot is written when it is created.
                    EnumerationQuery snapshots = Window(after, before);
                    snapshots.EventType = IncidentService.SnapshotEventType;
                    ids = (await database.Events.EnumerateAsync(snapshots, token).ConfigureAwait(false)).Objects
                        .Where(evt => String.Equals(evt.EntityType, IncidentService.IncidentEntityType, StringComparison.OrdinalIgnoreCase))
                        .Select(evt => evt.EntityId ?? String.Empty);
                    break;
            }
            return ids.Where(candidate => Expands(abbreviated, candidate)).Distinct(StringComparer.Ordinal).ToList();
        }

        private static EnumerationQuery Window(DateTime after, DateTime before)
        {
            return new EnumerationQuery { CreatedAfter = after, CreatedBefore = before, PageSize = _MaxCandidates };
        }

        private static async Task<bool> CallerCanSeeAsync(DatabaseDriver database, AuthContext caller, string id, CancellationToken token)
        {
            bool missionCaller = !String.IsNullOrEmpty(caller.MissionId);
            switch (id.Substring(0, 4))
            {
                case "msn_":
                    if (missionCaller)
                    {
                        Mission? mission = await database.Missions.ReadAsync(id, token).ConfigureAwait(false);
                        return mission != null && OwnershipPolicy.SameOwner(caller, mission.TenantId, mission.UserId);
                    }
                    return await CallerScopedRead.ReadMissionAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "vyg_":
                    if (missionCaller)
                    {
                        Voyage? voyage = await database.Voyages.ReadAsync(id, token).ConfigureAwait(false);
                        return voyage != null && OwnershipPolicy.SameOwner(caller, voyage.TenantId, voyage.UserId);
                    }
                    return await CallerScopedRead.ReadVoyageAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "obj_":
                    if (missionCaller)
                    {
                        Objective? objective = await database.Objectives.ReadAsync(id, token).ConfigureAwait(false);
                        return objective != null && OwnershipPolicy.SameOwner(caller, objective.TenantId, objective.UserId);
                    }
                    return await CallerScopedRead.ReadObjectiveAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "chk_":
                    if (missionCaller)
                    {
                        CheckRun? run = await database.CheckRuns.ReadAsync(id, null, token).ConfigureAwait(false);
                        return run != null && OwnershipPolicy.SameOwner(caller, run.TenantId, run.UserId);
                    }
                    return await CallerScopedRead.ReadCheckRunAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "mrg_":
                    MergeEntry? entry = await database.MergeEntries.ReadAsync(id, token).ConfigureAwait(false);
                    return entry != null && InScope(caller, missionCaller, entry.TenantId, entry.UserId);
                case "sig_":
                    Signal? signal = await database.Signals.ReadAsync(id, token).ConfigureAwait(false);
                    return signal != null && InScope(caller, missionCaller, signal.TenantId, signal.UserId);
                case "cpt_":
                    if (missionCaller)
                    {
                        Captain? captain = await database.Captains.ReadAsync(id, token).ConfigureAwait(false);
                        return captain != null && OwnershipPolicy.SameOwner(caller, captain.TenantId, captain.UserId);
                    }
                    return await CallerScopedRead.ReadCaptainAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "vsl_":
                    if (missionCaller)
                    {
                        Vessel? vessel = await database.Vessels.ReadAsync(id, token).ConfigureAwait(false);
                        return vessel != null && OwnershipPolicy.SameOwner(caller, vessel.TenantId, vessel.UserId);
                    }
                    return await CallerScopedRead.ReadVesselAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "flt_":
                    if (missionCaller)
                    {
                        Fleet? fleet = await database.Fleets.ReadAsync(id, token).ConfigureAwait(false);
                        return fleet != null && OwnershipPolicy.SameOwner(caller, fleet.TenantId, fleet.UserId);
                    }
                    return await CallerScopedRead.ReadFleetAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "dck_":
                    if (missionCaller)
                    {
                        Dock? dock = await database.Docks.ReadAsync(id, token).ConfigureAwait(false);
                        return dock != null && OwnershipPolicy.SameOwner(caller, dock.TenantId, dock.UserId);
                    }
                    return await CallerScopedRead.ReadDockAsync(database, caller, id, token).ConfigureAwait(false) != null;
                case "rel_":
                    Release? release = await CallerScopedRead.ReadReleaseAsync(database, caller, id, token).ConfigureAwait(false);
                    return release != null && (!missionCaller || OwnershipPolicy.SameOwner(caller, release.TenantId, release.UserId));
                case "dpl_":
                    Deployment? deployment = await CallerScopedRead.ReadDeploymentAsync(database, caller, id, token).ConfigureAwait(false);
                    return deployment != null && (!missionCaller || OwnershipPolicy.SameOwner(caller, deployment.TenantId, deployment.UserId));
                default:
                    Incident? incident = await new IncidentService(database).ReadAsync(caller, id, token).ConfigureAwait(false);
                    return incident != null && (!missionCaller || OwnershipPolicy.SameOwner(caller, incident.TenantId, incident.UserId));
            }
        }

        // A record type without its own scoped read follows the shared caller scope: a mission caller sees its
        // owner's records, a global administrator every record, a tenant administrator its tenant's records,
        // and any other caller the records it owns.
        private static bool InScope(AuthContext caller, bool missionCaller, string? tenantId, string? userId)
        {
            if (missionCaller) return OwnershipPolicy.SameOwner(caller, tenantId, userId);
            CallerRecordScope scope = CallerScopedRead.ResolveScope(caller);
            switch (scope.Kind)
            {
                case CallerRecordScopeKindEnum.Global:
                    return true;
                case CallerRecordScopeKindEnum.Tenant:
                    return OwnershipPolicy.SameTenant(tenantId, scope.TenantId);
                case CallerRecordScopeKindEnum.User:
                    return OwnershipPolicy.SameOwner(caller, tenantId, userId);
                default:
                    return false;
            }
        }

        #endregion
    }
}
