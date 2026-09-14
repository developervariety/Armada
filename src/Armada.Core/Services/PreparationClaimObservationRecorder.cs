namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule that turns preparation claim changes and claim delivery into durable
    /// observations. Repetition is counted only when the same claim, with the same statement,
    /// evidence, and anchors, is verified again.
    /// </summary>
    public static class PreparationClaimObservationRecorder
    {
        /// <summary>Capture the claim state of an objective before it changes.</summary>
        /// <param name="objective">Objective in its stored state.</param>
        /// <returns>Snapshot keyed by claim identifier.</returns>
        public static PreparationClaimSnapshot Capture(Objective objective)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            PreparationClaimSnapshot snapshot = new PreparationClaimSnapshot
            {
                SourceCommit = Commit(objective.Preparation?.Source),
                TargetCommit = Commit(objective.Preparation?.Target)
            };
            foreach (ObjectivePreparationClaim claim in (objective.Preparation?.Claims ?? new List<ObjectivePreparationClaim>())
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Id)))
            {
                if (snapshot.Claims.ContainsKey(claim.Id)) continue;
                snapshot.Claims[claim.Id] = new PreparationClaimSnapshotEntry(claim.State, claim.VerifiedUtc, Fingerprint(claim));
            }
            return snapshot;
        }

        /// <summary>
        /// Classify the observations implied by an objective's current claims compared with a prior
        /// snapshot. Only verified claims produce observations.
        /// </summary>
        /// <param name="prior">Snapshot before the change, or an empty snapshot for a new objective.</param>
        /// <param name="current">Objective after the change.</param>
        /// <returns>Observations to store.</returns>
        public static List<PreparationClaimObservation> Classify(PreparationClaimSnapshot prior, Objective current)
        {
            if (prior == null) throw new ArgumentNullException(nameof(prior));
            if (current == null) throw new ArgumentNullException(nameof(current));
            List<PreparationClaimObservation> observations = new List<PreparationClaimObservation>();
            string? sourceCommit = Commit(current.Preparation?.Source);
            string? targetCommit = Commit(current.Preparation?.Target);
            bool sourceMoved = !SameCommit(prior.SourceCommit, sourceCommit);
            bool targetMoved = !SameCommit(prior.TargetCommit, targetCommit);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectivePreparationClaim claim in current.Preparation?.Claims ?? new List<ObjectivePreparationClaim>())
            {
                if (claim == null || String.IsNullOrWhiteSpace(claim.Id) || !seen.Add(claim.Id)) continue;
                if (claim.State != ObjectivePreparationClaimStateEnum.Verified || !claim.VerifiedUtc.HasValue) continue;
                string fingerprint = Fingerprint(claim);
                bool anchorsMoved = (sourceMoved && (claim.DependsOn & ObjectivePreparationDependencyEnum.Source) != 0)
                    || (targetMoved && (claim.DependsOn & ObjectivePreparationDependencyEnum.Target) != 0);
                PreparationClaimObservationEnum? observation;
                if (!prior.Claims.TryGetValue(claim.Id, out PreparationClaimSnapshotEntry? before)) observation = PreparationClaimObservationEnum.Established;
                else if (before.State != ObjectivePreparationClaimStateEnum.Verified) observation = PreparationClaimObservationEnum.Revalidated;
                else if (before.VerifiedUtc.HasValue && claim.VerifiedUtc.Value <= before.VerifiedUtc.Value) observation = null;
                else if (!String.Equals(before.Fingerprint, fingerprint, StringComparison.Ordinal)) observation = PreparationClaimObservationEnum.Established;
                else if (anchorsMoved) observation = PreparationClaimObservationEnum.Revalidated;
                else observation = PreparationClaimObservationEnum.Reestablished;
                if (observation.HasValue)
                    observations.Add(NewObservation(current, claim, fingerprint, observation.Value, null, sourceCommit, targetCommit));
            }
            return observations;
        }

        /// <summary>Record the observations implied by a claim change.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="prior">Snapshot before the change.</param>
        /// <param name="current">Objective after the change was persisted.</param>
        /// <param name="logging">Optional logger for recording failures.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of stored observations.</returns>
        public static Task<int> RecordChangesAsync(DatabaseDriver database, PreparationClaimSnapshot prior, Objective current, LoggingModule? logging = null, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            return StoreAsync(database, Classify(prior, current), current.Id, logging, token);
        }

        /// <summary>Record that every verified claim of an objective was delivered to a voyage.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="objective">Objective whose claims were delivered.</param>
        /// <param name="voyageId">Voyage that received the claims.</param>
        /// <param name="logging">Optional logger for recording failures.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of stored observations.</returns>
        public static Task<int> RecordReuseAsync(DatabaseDriver database, Objective objective, string voyageId, LoggingModule? logging = null, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            string? sourceCommit = Commit(objective.Preparation?.Source);
            string? targetCommit = Commit(objective.Preparation?.Target);
            List<PreparationClaimObservation> observations = (objective.Preparation?.Claims ?? new List<ObjectivePreparationClaim>())
                .Where(claim => claim != null && !String.IsNullOrWhiteSpace(claim.Id) && claim.State == ObjectivePreparationClaimStateEnum.Verified)
                .GroupBy(claim => claim.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(claim => NewObservation(objective, claim, Fingerprint(claim), PreparationClaimObservationEnum.Reused, voyageId, sourceCommit, targetCommit))
                .ToList();
            return StoreAsync(database, observations, objective.Id, logging, token);
        }

        /// <summary>
        /// One-way SHA-256 fingerprint of a claim's kind, dependency, statement, and sorted evidence.
        /// </summary>
        /// <param name="claim">Claim to fingerprint.</param>
        /// <returns>Lower-case hexadecimal fingerprint.</returns>
        public static string Fingerprint(ObjectivePreparationClaim claim)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            StringBuilder builder = new StringBuilder();
            builder.Append(claim.Kind).Append('\n').Append(claim.DependsOn).Append('\n').Append((claim.Text ?? String.Empty).Trim());
            foreach (string link in (claim.EvidenceLinks ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal))
                builder.Append('\n').Append(link);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static async Task<int> StoreAsync(DatabaseDriver database, List<PreparationClaimObservation> observations, string objectiveId, LoggingModule? logging, CancellationToken token)
        {
            int stored = 0;
            foreach (PreparationClaimObservation observation in observations)
            {
                try
                {
                    await database.PreparationClaimObservations.CreateAsync(observation, token).ConfigureAwait(false);
                    stored++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logging?.Warn("[PreparationClaimObservationRecorder] could not record " + observation.Observation + " for claim "
                        + observation.ClaimId + " of objective " + objectiveId + " (" + ex.GetType().Name + "): " + ex.Message);
                }
            }
            return stored;
        }

        private static PreparationClaimObservation NewObservation(
            Objective objective,
            ObjectivePreparationClaim claim,
            string fingerprint,
            PreparationClaimObservationEnum observation,
            string? voyageId,
            string? sourceCommit,
            string? targetCommit)
        {
            return new PreparationClaimObservation
            {
                TenantId = objective.TenantId,
                UserId = objective.UserId,
                ObjectiveId = objective.Id,
                ClaimId = claim.Id,
                ClaimKind = claim.Kind,
                SourceFamily = ProductionSourceFamily.Resolve(objective),
                VoyageId = voyageId,
                SourceCommit = sourceCommit,
                TargetCommit = targetCommit,
                EvidenceFingerprint = fingerprint,
                Observation = observation,
                CreatedUtc = DateTime.UtcNow
            };
        }

        private static string? Commit(ObjectivePreparationAnchor? anchor) =>
            String.IsNullOrWhiteSpace(anchor?.ResolvedCommit) ? null : anchor!.ResolvedCommit!.Trim().ToLowerInvariant();

        private static bool SameCommit(string? left, string? right) => String.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>Claim state of an objective captured before a change.</summary>
    public sealed class PreparationClaimSnapshot
    {
        /// <summary>Prior claims by identifier.</summary>
        public Dictionary<string, PreparationClaimSnapshotEntry> Claims { get; } = new Dictionary<string, PreparationClaimSnapshotEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Prior resolved source commit.</summary>
        public string? SourceCommit { get; set; } = null;

        /// <summary>Prior resolved target commit.</summary>
        public string? TargetCommit { get; set; } = null;
    }

    /// <summary>One prior claim state.</summary>
    public sealed class PreparationClaimSnapshotEntry
    {
        /// <summary>Instantiate.</summary>
        /// <param name="state">Prior state.</param>
        /// <param name="verifiedUtc">Prior verification time.</param>
        /// <param name="fingerprint">Prior fingerprint.</param>
        public PreparationClaimSnapshotEntry(ObjectivePreparationClaimStateEnum state, DateTime? verifiedUtc, string fingerprint)
        {
            State = state;
            VerifiedUtc = verifiedUtc;
            Fingerprint = fingerprint;
        }

        /// <summary>Prior state.</summary>
        public ObjectivePreparationClaimStateEnum State { get; }

        /// <summary>Prior verification time.</summary>
        public DateTime? VerifiedUtc { get; }

        /// <summary>Prior fingerprint.</summary>
        public string Fingerprint { get; }
    }
}
