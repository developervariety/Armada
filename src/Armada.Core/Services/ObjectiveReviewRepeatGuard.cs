namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Stops only the objective whose latest two full-chain reviews reject frozen source.</summary>
    public static class ObjectiveReviewRepeatGuard
    {
        /// <summary>Stable non-overridable dispatch issue code.</summary>
        public const string IssueCode = "objective_repeat_review_stop";

        /// <summary>Hash the prepared input without its retry attestation or mutable status.</summary>
        public static string InputHash(Objective objective, string? sourceCommit)
        {
            string input = JsonSerializer.Serialize(new
            {
                objective.Title, objective.Description, objective.AcceptanceCriteria,
                objective.NonGoals, objective.SuggestedPipelineId, sourceCommit
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        }

        /// <summary>Return a blocking issue, or null. Reading never changes the board or objective.</summary>
        public static async Task<ObjectiveDispatchPreviewIssue?> EvaluateAsync(DatabaseDriver database, Objective objective,
            string? sourceCommit, CancellationToken token = default)
        {
            List<Mission> judges = new List<Mission>();
            Dictionary<string, List<Mission>> chains = new Dictionary<string, List<Mission>>(StringComparer.Ordinal);
            foreach (string id in objective.VoyageIds.Distinct(StringComparer.Ordinal))
            {
                List<Mission> missions = (await database.Missions.EnumerateByVoyageAsync(id, token).ConfigureAwait(false))
                    .Where(item => (item.TenantId ?? Constants.DefaultTenantId) == (objective.TenantId ?? Constants.DefaultTenantId) && (item.UserId ?? Constants.DefaultUserId) == (objective.UserId ?? Constants.DefaultUserId)).ToList();
                chains[id] = missions;
                Mission? judge = missions.Where(item => String.Equals(item.Persona, "Judge", StringComparison.OrdinalIgnoreCase)
                    && item.CompletedUtc.HasValue).OrderByDescending(item => item.CompletedUtc).FirstOrDefault();
                if (judge != null) judges.Add(judge);
            }
            judges = judges.OrderByDescending(item => item.CompletedUtc).Take(2).ToList();
            if (judges.Count < 2 || judges.Any(item => item.Status != MissionStatusEnum.Failed
                || !String.Equals(item.FailureReason, "Judge verdict: NEEDS_REVISION", StringComparison.Ordinal))) return null;

            string? frozen = null;
            foreach (Mission judge in judges)
            {
                Mission? first = chains[judge.VoyageId!].OrderBy(item => item.CreatedUtc).FirstOrDefault();
                EnumerationResult<ArmadaEvent> sourceEvents = await database.Events.EnumerateAsync(new EnumerationQuery
                { VoyageId = judge.VoyageId, EventType = VoyageDispatchOrigin.EventType, PageSize = 1 }, token).ConfigureAwait(false);
                VoyageDispatchOrigin? dispatch = sourceEvents.Objects
                    .Where(item => (item.TenantId ?? Constants.DefaultTenantId) == (objective.TenantId ?? Constants.DefaultTenantId) && (item.UserId ?? Constants.DefaultUserId) == (objective.UserId ?? Constants.DefaultUserId))
                    .Select(item => JsonSerializer.Deserialize<VoyageDispatchOrigin>(item.Payload ?? "{}")).FirstOrDefault();
                string? source = dispatch?.SourceCommit ?? first?.StartFromRef;
                if (String.IsNullOrWhiteSpace(source) || source.Length != 40 || !source.All(Uri.IsHexDigit)) return null;
                if (frozen != null && !String.Equals(frozen, source, StringComparison.OrdinalIgnoreCase)) return null;
                frozen = source;
            }
            Mission latest = judges[0];
            string? candidate = latest.CommitHash ?? chains[latest.VoyageId!]
                .Where(item => !String.IsNullOrWhiteSpace(item.CommitHash)).OrderByDescending(item => item.CompletedUtc)
                .Select(item => item.CommitHash).FirstOrDefault() ?? frozen;
            string inputHash = InputHash(objective, sourceCommit);
            ObjectiveReviewRetry? receipt = objective.Preparation?.Preflight?.ReviewRetry;
            EnumerationResult<ArmadaEvent> origins = await database.Events.EnumerateAsync(new EnumerationQuery
            {
                VoyageId = latest.VoyageId, EventType = VoyageDispatchOrigin.EventType, PageSize = 1
            }, token).ConfigureAwait(false);
            VoyageDispatchOrigin? origin = origins.Objects.Where(item => (item.TenantId ?? Constants.DefaultTenantId) == (objective.TenantId ?? Constants.DefaultTenantId) && (item.UserId ?? Constants.DefaultUserId) == (objective.UserId ?? Constants.DefaultUserId))
                .Select(item => JsonSerializer.Deserialize<VoyageDispatchOrigin>(item.Payload ?? "{}")).FirstOrDefault();
            bool changed = origin?.InputHash != null
                ? !String.Equals(origin.InputHash, inputHash, StringComparison.Ordinal)
                : !String.Equals(sourceCommit, frozen, StringComparison.OrdinalIgnoreCase);
            if (changed && receipt != null && receipt.JudgeMissionId == latest.Id
                && String.Equals(receipt.CandidateHash, candidate, StringComparison.OrdinalIgnoreCase)
                && receipt.InputHash == inputHash && receipt.AllFindingsAddressed
                && !String.IsNullOrWhiteSpace(receipt.ChangedInput) && !String.IsNullOrWhiteSpace(receipt.FindingsEvidence)) return null;
            return new ObjectiveDispatchPreviewIssue
            {
                Code = IssueCode, Area = "review", Severity = ReadinessSeverityEnum.Error,
                RelatedValue = latest.Id,
                Message = "Two consecutive Judge NEEDS_REVISION verdicts rejected frozen source " + frozen
                    + ". Stop this objective before another full-chain dispatch. Preserve candidate " + candidate
                    + "; record changed input and evidence for every named finding in preflight ReviewRetry. Latest Judge "
                    + latest.Id + "; current input hash " + inputHash + "."
            };
        }
    }
}
