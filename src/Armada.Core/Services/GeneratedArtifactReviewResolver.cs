namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>Find the nearest same-owner candidate review on a stage's dependency chain.</summary>
    public static class GeneratedArtifactReviewResolver
    {
        /// <summary>Resolve the Judge binding for a terminal stage without copying it onto that stage.</summary>
        public static async Task<Mission?> ResolveJudgeAsync(DatabaseDriver database, Mission mission,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? current = mission;
            while (current != null)
            {
                if (!visited.Add(current.Id)) throw new InvalidOperationException("generated_candidate_dependency_cycle");
                if (!OwnershipPolicy.SameTenant(mission.TenantId, current.TenantId)
                    || !String.Equals(OwnershipPolicy.UserOfRecord(mission.UserId), OwnershipPolicy.UserOfRecord(current.UserId), StringComparison.Ordinal)
                    || !String.Equals(mission.VesselId, current.VesselId, StringComparison.Ordinal)
                    || !String.Equals(mission.VoyageId, current.VoyageId, StringComparison.Ordinal)) return null;
                if (current.GeneratedArtifactReview != null)
                {
                    if (!String.Equals(current.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("generated_candidate_binding_owner_invalid");
                    return current;
                }
                if (String.IsNullOrWhiteSpace(current.DependsOnMissionId)) return null;
                current = await database.Missions.ReadAsync(current.DependsOnMissionId, token).ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>Refuse an unresolved, stale or unapproved binding before the target ref can move.</summary>
        public static async Task<string?> PublicationRefusalAsync(DatabaseDriver database, Mission mission,
            string targetCommit, string sourceCommit, CancellationToken token = default)
        {
            Mission? judge = await ResolveJudgeAsync(database, mission, token).ConfigureAwait(false);
            if (judge == null) return null;
            if (judge.HeldForOperatorReview) return "generated_candidate_operator_hold";
            return GeneratedArtifactReviewRule.PublicationRefusal(judge, targetCommit, sourceCommit);
        }
    }
}
