namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Limits captain evidence reads to records linked to the calling mission. A mission token never
    /// inherits its owner's operator privileges, and every record must have the caller's tenant and owner.
    /// The links are: the mission's report chain (its voyage, direct dependency, direct parent and marked
    /// rescue root); the objectives that authorize the mission or a direct source, with their ancestor
    /// chain, blockers and direct children; the objectives, missions and voyages those authorizing and
    /// ancestor objectives cite by id in their description, criteria or evidence links; and the missions
    /// and voyages any of those objectives list. Citations are followed one hop only. A Challenger runs outside
    /// any voyage, so its direct source is the Judge mission it challenges, proven by that Judge's dispatch
    /// record; it then reaches what a source of the Judge's voyage reaches.
    /// </summary>
    internal static class McpMissionEvidenceScope
    {
        #region Private-Members

        private const int _MaxAncestorDepth = 32;

        private const int _MaxJudgeEvents = 200;

        private static readonly Regex _CitedId = new Regex(
            @"(?<![A-Za-z0-9_])(?:obj|msn|vyg)_[A-Za-z0-9]+(?:_[A-Za-z0-9]+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>Read a report source within the caller's evidence scope.</summary>
        public static async Task<Mission?> ReadMissionAsync(DatabaseDriver database, AuthContext caller, string id)
        {
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, id).ConfigureAwait(false);
            if (resolved.Ambiguous) return null;
            id = resolved.Id;
            if (String.IsNullOrEmpty(caller.MissionId))
                return await CallerScopedRead.ReadMissionAsync(database, caller, id).ConfigureAwait(false);

            MissionReadScope? scope = await OpenAsync(database, caller).ConfigureAwait(false);
            if (scope == null) return null;
            // ID reads allow legacy null ownership; the scope's owner rule is mandatory before any record leaves it.
            Mission? target = await database.Missions.ReadAsync(id).ConfigureAwait(false);
            return target != null && await scope.CanReadMissionAsync(target).ConfigureAwait(false) ? target : null;
        }

        /// <summary>Read an objective linked to the caller's mission, without previewing its evidence.</summary>
        public static async Task<Objective?> ReadObjectiveAsync(DatabaseDriver database, AuthContext caller, string id)
        {
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, id).ConfigureAwait(false);
            if (resolved.Ambiguous) return null;
            id = resolved.Id;
            Mission? anchor = await ReadAnchorAsync(database, caller).ConfigureAwait(false);
            if (anchor == null) return null;
            Objective? target = await database.Objectives.ReadAsync(id).ConfigureAwait(false);
            if (target == null || !SameOwner(caller, target.TenantId, target.UserId)) return null;
            EvidenceLinks links = await ReadEvidenceLinksAsync(database, caller, anchor).ConfigureAwait(false);
            return links.Objectives.ContainsKey(target.Id) ? target : null;
        }

        /// <summary>
        /// Read a voyage linked to the caller's mission: its own voyage, the voyage of a direct dependency or
        /// parent, a voyage listed or cited by a linked objective, or a voyage whose brief names one.
        /// </summary>
        public static async Task<Voyage?> ReadVoyageAsync(DatabaseDriver database, AuthContext caller, string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, id).ConfigureAwait(false);
            if (resolved.Ambiguous) return null;
            id = resolved.Id;
            MissionReadScope? scope = await OpenAsync(database, caller).ConfigureAwait(false);
            if (scope == null) return null;
            Voyage? voyage = await CallerScopedRead.ReadVoyageAsync(database, OwnerScopeOf(caller), id).ConfigureAwait(false);
            return voyage != null && await scope.CanReadVoyageAsync(voyage).ConfigureAwait(false) ? voyage : null;
        }

        /// <summary>
        /// Open the evidence scope of the calling mission once. The per-id mission and voyage reads ask it, and
        /// a listing asks it for every record, so a listing shows exactly what the per-id reads return.
        /// Null when the caller holds no mission credential or its own mission is outside its owner scope.
        /// </summary>
        public static async Task<MissionReadScope?> OpenAsync(DatabaseDriver database, AuthContext caller)
        {
            Mission? anchor = await ReadAnchorAsync(database, caller).ConfigureAwait(false);
            return anchor == null ? null : new MissionReadScope(database, caller, anchor);
        }

        /// <summary>
        /// The caller as its mission owner, with no administrator rights. A mission token never inherits its
        /// owner's operator privileges, so every caller-scoped read made for a mission caller uses this identity.
        /// </summary>
        public static AuthContext OwnerScopeOf(AuthContext caller)
        {
            return AuthContext.Authenticated(
                OwnershipPolicy.TenantOf(caller), OwnershipPolicy.UserOf(caller), false, false, caller.AuthMethod ?? "Session");
        }

        /// <summary>
        /// Ids of every objective the calling mission may read, so a listing shows exactly what
        /// <see cref="ReadObjectiveAsync"/> returns. Empty when the caller holds no mission credential.
        /// </summary>
        public static async Task<HashSet<string>> ReadLinkedObjectiveIdsAsync(DatabaseDriver database, AuthContext caller)
        {
            Mission? anchor = await ReadAnchorAsync(database, caller).ConfigureAwait(false);
            if (anchor == null) return new HashSet<string>(StringComparer.Ordinal);
            EvidenceLinks links = await ReadEvidenceLinksAsync(database, caller, anchor).ConfigureAwait(false);
            return new HashSet<string>(links.Objectives.Keys, StringComparer.Ordinal);
        }

        /// <summary>
        /// Return the id of the objective that authorizes the caller's own mission: one that lists the
        /// mission, or else one that lists its voyage. Null when the caller holds no mission credential or
        /// no objective authorizes it.
        /// </summary>
        public static async Task<string?> FindOwnObjectiveIdAsync(DatabaseDriver database, AuthContext caller)
        {
            Mission? anchor = await ReadAnchorAsync(database, caller).ConfigureAwait(false);
            if (anchor == null) return null;
            List<Objective> owned = await ReadOwnedObjectivesAsync(database, caller).ConfigureAwait(false);
            Objective? direct = owned.FirstOrDefault(objective => objective.MissionIds.Contains(anchor.Id, StringComparer.Ordinal));
            if (direct != null) return direct.Id;
            Objective? byVoyage = owned.FirstOrDefault(objective => Authorizes(objective, anchor));
            return byVoyage?.Id;
        }

        /// <summary>Explain a refused mission read without revealing another owner's record.</summary>
        public static async Task<McpScopeDenial> DenyMissionAsync(DatabaseDriver database, AuthContext caller, string? id)
        {
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, id).ConfigureAwait(false);
            if (resolved.Ambiguous) return McpScopeDenial.CreateAmbiguous("Mission not found", resolved.Candidates);
            id = resolved.Id;
            Mission? record = String.IsNullOrWhiteSpace(id) ? null : await database.Missions.ReadAsync(id).ConfigureAwait(false);
            return McpScopeDenial.Create("Mission not found", IsOwnedOutOfScope(caller, record?.TenantId, record?.UserId, record != null));
        }

        /// <summary>Explain a refused objective read without revealing another owner's record.</summary>
        public static async Task<McpScopeDenial> DenyObjectiveAsync(DatabaseDriver database, AuthContext caller, string? id)
        {
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, id).ConfigureAwait(false);
            if (resolved.Ambiguous) return McpScopeDenial.CreateAmbiguous("Objective not found", resolved.Candidates);
            id = resolved.Id;
            Objective? record = String.IsNullOrWhiteSpace(id) ? null : await database.Objectives.ReadAsync(id).ConfigureAwait(false);
            return McpScopeDenial.Create("Objective not found", IsOwnedOutOfScope(caller, record?.TenantId, record?.UserId, record != null));
        }

        /// <summary>Explain a refused voyage read without revealing another owner's record.</summary>
        public static async Task<McpScopeDenial> DenyVoyageAsync(DatabaseDriver database, AuthContext caller, string? id)
        {
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, id).ConfigureAwait(false);
            if (resolved.Ambiguous) return McpScopeDenial.CreateAmbiguous("Voyage not found", resolved.Candidates);
            id = resolved.Id;
            Voyage? record = String.IsNullOrWhiteSpace(id) ? null : await database.Voyages.ReadAsync(id).ConfigureAwait(false);
            return McpScopeDenial.Create("Voyage not found", IsOwnedOutOfScope(caller, record?.TenantId, record?.UserId, record != null));
        }

        /// <summary>Check mission-record ownership without traversing the caller's report chain.</summary>
        public static bool SameOwner(AuthContext caller, string? tenantId, string? userId)
        {
            return OwnershipPolicy.SameOwner(caller, tenantId, userId);
        }

        #endregion

        #region Private-Methods

        private static bool IsOwnedOutOfScope(AuthContext caller, string? tenantId, string? userId, bool exists)
        {
            // Only a mission caller gets the out-of-scope reason; any other caller reads through its own scope.
            return exists && !String.IsNullOrEmpty(caller.MissionId) && SameOwner(caller, tenantId, userId);
        }

        private static bool VoyageIsLinked(EvidenceLinks links, Voyage voyage)
        {
            if (links.VoyageIds.Contains(voyage.Id)) return true;
            string? briefObjectiveId = VoyageObjectiveResolver.ReadBriefObjectiveId(voyage.Description);
            return briefObjectiveId != null && links.Objectives.ContainsKey(briefObjectiveId);
        }

        private static async Task<EvidenceLinks> ReadEvidenceLinksAsync(DatabaseDriver database, AuthContext caller, Mission anchor)
        {
            EvidenceLinks links = new EvidenceLinks();
            List<Objective> ownedList = await ReadOwnedObjectivesAsync(database, caller).ConfigureAwait(false);
            Dictionary<string, Objective> owned = new Dictionary<string, Objective>(StringComparer.Ordinal);
            foreach (Objective objective in ownedList) owned[objective.Id] = objective;

            List<Mission> sources = new List<Mission> { anchor };
            foreach (string? sourceId in new[] { anchor.DependsOnMissionId, anchor.ParentMissionId })
            {
                if (String.IsNullOrWhiteSpace(sourceId)) continue;
                Mission? source = await database.Missions.ReadAsync(sourceId).ConfigureAwait(false);
                if (source != null && SameOwner(caller, source.TenantId, source.UserId)) sources.Add(source);
            }
            Mission? challenged = await ReadChallengedJudgeAsync(database, caller, anchor).ConfigureAwait(false);
            if (challenged != null)
            {
                sources.Add(challenged);
                links.MissionIds.Add(challenged.Id);
            }

            Dictionary<string, Objective> authorizing = new Dictionary<string, Objective>(StringComparer.Ordinal);
            foreach (Objective objective in ownedList)
            {
                if (sources.Any(source => Authorizes(objective, source))) authorizing[objective.Id] = objective;
            }
            foreach (Mission source in sources)
            {
                if (String.IsNullOrEmpty(source.VoyageId)) continue;
                links.VoyageIds.Add(source.VoyageId);
                Voyage? voyage = await database.Voyages.ReadAsync(source.VoyageId).ConfigureAwait(false);
                string? briefObjectiveId = VoyageObjectiveResolver.ReadBriefObjectiveId(voyage?.Description);
                if (briefObjectiveId != null && owned.TryGetValue(briefObjectiveId, out Objective? named))
                    authorizing[named.Id] = named;
            }

            Dictionary<string, Objective> citing = new Dictionary<string, Objective>(authorizing, StringComparer.Ordinal);
            foreach (Objective objective in authorizing.Values)
            {
                HashSet<string> chain = new HashSet<string>(StringComparer.Ordinal) { objective.Id };
                string? parentId = objective.ParentObjectiveId;
                for (int depth = 0; depth < _MaxAncestorDepth && !String.IsNullOrWhiteSpace(parentId); depth++)
                {
                    if (!chain.Add(parentId!) || !owned.TryGetValue(parentId!, out Objective? parent)) break;
                    citing[parent.Id] = parent;
                    parentId = parent.ParentObjectiveId;
                }
            }

            foreach (Objective objective in citing.Values) links.Objectives[objective.Id] = objective;
            foreach (Objective objective in ownedList)
            {
                if (!String.IsNullOrWhiteSpace(objective.ParentObjectiveId) && authorizing.ContainsKey(objective.ParentObjectiveId))
                    links.Objectives[objective.Id] = objective;
            }
            foreach (Objective objective in citing.Values)
            {
                foreach (string blockerId in objective.BlockedByObjectiveIds)
                {
                    if (owned.TryGetValue(blockerId, out Objective? blocker)) links.Objectives[blocker.Id] = blocker;
                }
                foreach (string cited in CitedIds(objective))
                {
                    // A cited abbreviation links the one record it resolves to, the same as the full id.
                    string citedId = cited;
                    if (citedId.StartsWith("obj_", StringComparison.Ordinal))
                    {
                        AbbreviatedIdResolution objectiveId = AbbreviatedIdResolver.ResolveAmong(citedId, owned.Keys);
                        if (!objectiveId.Ambiguous && owned.TryGetValue(objectiveId.Id, out Objective? citedObjective))
                            links.Objectives[citedObjective.Id] = citedObjective;
                    }
                    else if (citedId.StartsWith("msn_", StringComparison.Ordinal))
                    {
                        AbbreviatedIdResolution missionId = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, citedId).ConfigureAwait(false);
                        if (!missionId.Ambiguous) links.MissionIds.Add(missionId.Id);
                    }
                    else
                    {
                        AbbreviatedIdResolution voyageId = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, citedId).ConfigureAwait(false);
                        if (!voyageId.Ambiguous) links.VoyageIds.Add(voyageId.Id);
                    }
                }
            }

            foreach (Objective objective in links.Objectives.Values)
            {
                foreach (string missionId in objective.MissionIds) links.MissionIds.Add(missionId);
                foreach (string voyageId in objective.VoyageIds) links.VoyageIds.Add(voyageId);
            }
            return links;
        }

        // The brief line only names a candidate; the Judge's dispatch record must name this Challenger, so a
        // caller cannot widen its scope by writing a Judge id into a description.
        private static async Task<Mission?> ReadChallengedJudgeAsync(DatabaseDriver database, AuthContext caller, Mission anchor)
        {
            if (!PersonaCatalog.Matches(anchor.Persona, PersonaCatalog.Challenger)) return null;
            string? judgeId = JudgeChallenge.ReadJudgeMissionId(anchor.Description);
            if (judgeId == null || String.Equals(judgeId, anchor.Id, StringComparison.Ordinal)) return null;
            Mission? judge = await database.Missions.ReadAsync(judgeId).ConfigureAwait(false);
            if (judge == null || !SameOwner(caller, judge.TenantId, judge.UserId)) return null;
            List<ArmadaEvent> events = await database.Events.EnumerateByMissionAsync(judge.Id, _MaxJudgeEvents).ConfigureAwait(false);
            bool dispatchedThisChallenger = events.Any(evt =>
                String.Equals(evt.EventType, JudgeChallenge.DispatchedEvent, StringComparison.Ordinal)
                && String.Equals(JudgeChallenge.ReadDispatchRecord(evt.Payload).ChallengerMissionId, anchor.Id, StringComparison.Ordinal));
            return dispatchedThisChallenger ? judge : null;
        }

        private static IEnumerable<string> CitedIds(Objective objective)
        {
            List<string> texts = new List<string>();
            if (!String.IsNullOrEmpty(objective.Description)) texts.Add(objective.Description);
            texts.AddRange(objective.AcceptanceCriteria);
            texts.AddRange(objective.EvidenceLinks);
            foreach (string text in texts)
            {
                if (String.IsNullOrEmpty(text)) continue;
                foreach (Match match in _CitedId.Matches(text)) yield return match.Value;
            }
        }

        private static async Task<List<Objective>> ReadOwnedObjectivesAsync(DatabaseDriver database, AuthContext caller)
        {
            // Enumerate by tenant and apply the owner rule here, so an objective stored without a user still
            // belongs to the default owner.
            List<Objective> objectives = OwnershipPolicy.TenantOf(caller) == Armada.Core.Constants.DefaultTenantId
                ? await database.Objectives.EnumerateAsync().ConfigureAwait(false)
                : await database.Objectives.EnumerateAsync(OwnershipPolicy.TenantOf(caller)).ConfigureAwait(false);
            return objectives.Where(objective => SameOwner(caller, objective.TenantId, objective.UserId)).ToList();
        }

        private static async Task<Mission?> FindRescueRootInDependencyChainAsync(
            DatabaseDriver database,
            AuthContext caller,
            Mission anchor)
        {
            if (String.IsNullOrWhiteSpace(anchor.VoyageId) || String.IsNullOrWhiteSpace(anchor.VesselId)) return null;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal) { anchor.Id };
            Mission current = anchor;
            while (!String.IsNullOrWhiteSpace(current.DependsOnMissionId))
            {
                string predecessorId = current.DependsOnMissionId!;
                if (!visited.Add(predecessorId)) return null;
                Mission? predecessor = await database.Missions.ReadAsync(predecessorId).ConfigureAwait(false);
                if (predecessor == null
                    || !SameOwner(caller, predecessor.TenantId, predecessor.UserId)
                    || !String.Equals(anchor.VoyageId, predecessor.VoyageId, StringComparison.Ordinal)
                    || !String.Equals(anchor.VesselId, predecessor.VesselId, StringComparison.Ordinal))
                    return null;

                if (RescueMissionMarker.CarriesDescriptionMarker(predecessor.Description)
                    && !String.IsNullOrWhiteSpace(predecessor.ParentMissionId)
                    && String.IsNullOrWhiteSpace(predecessor.DependsOnMissionId))
                    return predecessor;
                current = predecessor;
            }
            return null;
        }

        private static bool SameRescueVessel(Mission rescueStage, Mission target)
        {
            return !String.IsNullOrWhiteSpace(rescueStage.VesselId)
                && String.Equals(rescueStage.VesselId, target.VesselId, StringComparison.Ordinal);
        }

        private static bool Authorizes(Objective objective, Mission mission)
        {
            return objective.MissionIds.Contains(mission.Id, StringComparer.Ordinal)
                || (!String.IsNullOrEmpty(mission.VoyageId)
                    && objective.VoyageIds.Contains(mission.VoyageId, StringComparer.Ordinal));
        }

        private static async Task<Mission?> ReadAnchorAsync(DatabaseDriver database, AuthContext caller)
        {
            if (String.IsNullOrEmpty(caller.MissionId)) return null;
            Mission? anchor = await database.Missions.ReadAsync(caller.MissionId).ConfigureAwait(false);
            return anchor != null && SameOwner(caller, anchor.TenantId, anchor.UserId) ? anchor : null;
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// The evidence scope of one calling mission. The links are read on first need and kept, and each
        /// voyage's link verdict is kept, so asking about many records costs one link read.
        /// </summary>
        internal sealed class MissionReadScope
        {
            private readonly DatabaseDriver _Database;
            private readonly AuthContext _Caller;
            private readonly Mission _Anchor;
            private readonly Dictionary<string, bool> _LinkedVoyages = new Dictionary<string, bool>(StringComparer.Ordinal);
            private EvidenceLinks? _Links;
            private bool _RescueRootRead;
            private Mission? _RescueRoot;

            /// <summary>Instantiate for one caller and its own mission.</summary>
            public MissionReadScope(DatabaseDriver database, AuthContext caller, Mission anchor)
            {
                _Database = database ?? throw new ArgumentNullException(nameof(database));
                _Caller = caller ?? throw new ArgumentNullException(nameof(caller));
                _Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            }

            /// <summary>True when the calling mission may read the mission.</summary>
            public async Task<bool> CanReadMissionAsync(Mission target)
            {
                if (target == null || !SameOwner(_Caller, target.TenantId, target.UserId)) return false;
                if (target.Id == _Anchor.Id
                    || (!String.IsNullOrEmpty(_Anchor.VoyageId) && target.VoyageId == _Anchor.VoyageId)
                    || target.Id == _Anchor.DependsOnMissionId
                    || target.Id == _Anchor.ParentMissionId)
                    return true;
                Mission? rescueRoot = await ReadRescueRootAsync().ConfigureAwait(false);
                if (rescueRoot != null
                    && rescueRoot.ParentMissionId == target.Id
                    && SameRescueVessel(_Anchor, target))
                    return true;

                EvidenceLinks links = await ReadLinksAsync().ConfigureAwait(false);
                if (links.MissionIds.Contains(target.Id)) return true;
                return !String.IsNullOrEmpty(target.VoyageId)
                    && await IsVoyageLinkedAsync(target.VoyageId).ConfigureAwait(false);
            }

            /// <summary>True when the calling mission may read the voyage.</summary>
            public async Task<bool> CanReadVoyageAsync(Voyage voyage)
            {
                if (voyage == null || !SameOwner(_Caller, voyage.TenantId, voyage.UserId)) return false;
                if (String.Equals(_Anchor.VoyageId, voyage.Id, StringComparison.Ordinal)) return true;
                return await IsVoyageLinkedAsync(voyage).ConfigureAwait(false);
            }

            /// <summary>
            /// Record the link verdict of voyages a listing has already read, so asking about their missions
            /// reads no voyage again.
            /// </summary>
            public async Task RememberVoyagesAsync(IEnumerable<Voyage> voyages)
            {
                if (voyages == null) return;
                foreach (Voyage voyage in voyages)
                {
                    if (voyage != null) await IsVoyageLinkedAsync(voyage).ConfigureAwait(false);
                }
            }

            private async Task<bool> IsVoyageLinkedAsync(Voyage voyage)
            {
                if (_LinkedVoyages.TryGetValue(voyage.Id, out bool known)) return known;
                EvidenceLinks links = await ReadLinksAsync().ConfigureAwait(false);
                bool linked = VoyageIsLinked(links, voyage);
                _LinkedVoyages[voyage.Id] = linked;
                return linked;
            }

            private async Task<bool> IsVoyageLinkedAsync(string voyageId)
            {
                if (_LinkedVoyages.TryGetValue(voyageId, out bool known)) return known;
                EvidenceLinks links = await ReadLinksAsync().ConfigureAwait(false);
                if (links.VoyageIds.Contains(voyageId))
                {
                    _LinkedVoyages[voyageId] = true;
                    return true;
                }
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId).ConfigureAwait(false);
                bool linked = voyage != null && VoyageIsLinked(links, voyage);
                _LinkedVoyages[voyageId] = linked;
                return linked;
            }

            private async Task<EvidenceLinks> ReadLinksAsync()
            {
                if (_Links == null) _Links = await ReadEvidenceLinksAsync(_Database, _Caller, _Anchor).ConfigureAwait(false);
                return _Links;
            }

            private async Task<Mission?> ReadRescueRootAsync()
            {
                if (!_RescueRootRead)
                {
                    _RescueRoot = await FindRescueRootInDependencyChainAsync(_Database, _Caller, _Anchor).ConfigureAwait(false);
                    _RescueRootRead = true;
                }
                return _RescueRoot;
            }
        }

        /// <summary>The records linked to one calling mission.</summary>
        private sealed class EvidenceLinks
        {
            /// <summary>Linked objectives by id.</summary>
            public Dictionary<string, Objective> Objectives { get; } = new Dictionary<string, Objective>(StringComparer.Ordinal);

            /// <summary>Missions listed or cited by linked objectives.</summary>
            public HashSet<string> MissionIds { get; } = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Voyages of the direct sources, and voyages listed or cited by linked objectives.</summary>
            public HashSet<string> VoyageIds { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        #endregion
    }
}
