namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Renders the server-authoritative objective brief used by every execution path.
    /// </summary>
    public static class ObjectiveBriefRenderer
    {
        public const int DefaultMaxChars = 16000;
        public const int DefaultMaxPreparationChars = 8000;
        private const int _MaxItemChars = 1200;

        /// <summary>
        /// Render one deterministic, bounded objective brief.
        /// </summary>
        public static string Render(Objective objective, int maxChars = DefaultMaxChars)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            if (maxChars < 256) throw new ArgumentOutOfRangeException(nameof(maxChars), "The objective brief limit must be at least 256 characters.");

            string startMarker = StartMarker(objective.Id);
            const string endMarker = "<!-- /armada-objective-brief -->";
            string preparation = RenderPreparation(
                objective,
                Math.Min(DefaultMaxPreparationChars, Math.Max(80, maxChars / 2)));
            int reservedSuffixChars = endMarker.Length + 2;
            if (!String.IsNullOrWhiteSpace(preparation)) reservedSuffixChars += preparation.Length + 2;
            int coreLimit = Math.Max(0, maxChars - reservedSuffixChars);
            StringBuilder result = new StringBuilder(Math.Min(maxChars, 4096));
            AppendAtomic(result, startMarker, coreLimit, false);
            AppendAtomic(result, "# Objective Brief", coreLimit, true);
            AppendAtomic(result, "Objective: " + BoundItem(objective.Title), coreLimit, true);

            AppendTextSection(result, "## Scope", objective.Description, coreLimit);
            AppendListSection(result, "## Acceptance Criteria", objective.AcceptanceCriteria, coreLimit);
            AppendListSection(result, "## Non-Goals", objective.NonGoals, coreLimit);

            AppendAtomic(result, preparation, maxChars - endMarker.Length - 2, true);

            AppendAtomic(result, endMarker, maxChars, true);
            return result.ToString().TrimEnd();
        }

        /// <summary>
        /// Append the authoritative brief after operator-specific instructions. The marker makes
        /// augmentation idempotent when a request is validated or retried more than once.
        /// </summary>
        public static string AppendToMissionDescription(string? existing, Objective objective, int maxChars = DefaultMaxChars)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            string current = existing?.TrimEnd() ?? String.Empty;
            if (current.Contains(StartMarker(objective.Id), StringComparison.Ordinal)) return current;
            string brief = Render(objective, maxChars);
            return String.IsNullOrWhiteSpace(current) ? brief : current + Environment.NewLine + Environment.NewLine + brief;
        }

        private static string RenderPreparation(Objective objective, int maxChars)
        {
            const string omittedMarker = "### Incomplete Preparation\n\n- [additional preparation sections omitted]";
            int contentLimit = Math.Max(0, maxChars - omittedMarker.Length - 2);
            StringBuilder result = new StringBuilder(Math.Min(maxChars, 2048));
            ObjectivePreparation preparationModel = objective.Preparation ?? new ObjectivePreparation();
            List<ObjectivePreparationClaim> claims = preparationModel.Claims ?? new List<ObjectivePreparationClaim>();
            bool hasAnchors = HasAnchor(preparationModel.Source) || HasAnchor(preparationModel.Target);
            bool hasContent = hasAnchors
                || preparationModel.RequiredForDispatch
                || (preparationModel.RequiredSiblingInputs?.Count ?? 0) > 0
                || claims.Any(claim => claim != null && !String.IsNullOrWhiteSpace(claim.Text))
                || !String.IsNullOrWhiteSpace(objective.RefinementSummary)
                || objective.RolloutConstraints.Any(item => !String.IsNullOrWhiteSpace(item))
                || objective.EvidenceLinks.Any(item => !String.IsNullOrWhiteSpace(item));
            if (!hasContent) return String.Empty;

            bool complete = AppendAtomic(result, "## Prepared Research", contentLimit, false);
            if (preparationModel.RequiredForDispatch)
            {
                List<string> gates = new List<string>
                {
                    "Dispatch requires verified preparation."
                };
                if (preparationModel.RequiredClaimKinds?.Count > 0)
                    gates.Add("Required claim kinds: " + String.Join(", ", preparationModel.RequiredClaimKinds) + ".");
                complete &= AppendListSection(result, "### Required Dispatch Gates", gates, contentLimit);
            }
            if (hasAnchors)
            {
                List<string> anchors = new List<string>();
                if (HasAnchor(preparationModel.Source)) anchors.Add("Source: " + RenderAnchor(preparationModel.Source!));
                if (HasAnchor(preparationModel.Target)) anchors.Add("Target: " + RenderAnchor(preparationModel.Target!));
                complete &= AppendListSection(result, "### Source and Target Anchors", anchors, contentLimit);
            }

            if (preparationModel.RequiredSiblingInputs?.Count > 0)
            {
                complete &= AppendListSection(result, "### Required Sibling Inputs",
                    preparationModel.RequiredSiblingInputs.Where(item => item != null).Select(RenderSiblingInput),
                    contentLimit);
            }

            foreach (IGrouping<ObjectivePreparationClaimKindEnum, ObjectivePreparationClaim> group in claims
                .Where(claim => claim != null && !String.IsNullOrWhiteSpace(claim.Text))
                .GroupBy(claim => claim.Kind)
                .OrderBy(group => (int)group.Key))
            {
                List<string> renderedClaims = group.Select(RenderClaim).ToList();
                complete &= AppendListSection(result, "### " + ClaimHeading(group.Key), renderedClaims, contentLimit);
            }

            complete &= AppendTextSection(result, "### Refinement Summary", objective.RefinementSummary, contentLimit);
            complete &= AppendListSection(result, "### Rollout and Execution Constraints", objective.RolloutConstraints, contentLimit);
            complete &= AppendListSection(result, "### Evidence", objective.EvidenceLinks, contentLimit);
            if (!complete) AppendAtomic(result, omittedMarker, maxChars, true);
            return result.ToString().TrimEnd();
        }

        private static string RenderClaim(ObjectivePreparationClaim claim)
        {
            string state = claim.State == ObjectivePreparationClaimStateEnum.NeedsRecheck
                ? "RECHECK REQUIRED" + (String.IsNullOrWhiteSpace(claim.InvalidationReason) ? String.Empty : ": " + claim.InvalidationReason.Trim())
                : "Verified";
            string evidence = claim.EvidenceLinks.Count == 0
                ? String.Empty
                : " Evidence: " + String.Join(", ", claim.EvidenceLinks.Select(BoundItem)) + ".";
            return "[" + state + "] " + BoundItem(claim.Text) + evidence;
        }

        private static string RenderSiblingInput(ObjectivePreparationSiblingInput input)
        {
            string artifacts = input.RequiredArtifactPaths?.Count > 0
                ? "; artifacts: " + String.Join(", ", input.RequiredArtifactPaths.Select(BoundItem))
                : String.Empty;
            return "vessel `" + BoundItem(input.VesselRef) + "` at `" + BoundItem(input.RelativePath) + "`" + artifacts;
        }

        private static string ClaimHeading(ObjectivePreparationClaimKindEnum kind)
        {
            return kind switch
            {
                ObjectivePreparationClaimKindEnum.SourcePath => "Readable Source Paths",
                ObjectivePreparationClaimKindEnum.DispatchEntryPoint => "Dispatch Entry Points",
                ObjectivePreparationClaimKindEnum.ReuseType => "Implementation Types to Reuse",
                ObjectivePreparationClaimKindEnum.CatalogueInput => "Catalogue Inputs",
                ObjectivePreparationClaimKindEnum.ProvisioningRequirement => "Provisioning Requirements",
                ObjectivePreparationClaimKindEnum.ResponseRule => "Response Predicates and Result Rules",
                ObjectivePreparationClaimKindEnum.CleanupRequirement => "Cleanup Requirements",
                ObjectivePreparationClaimKindEnum.ConsumerObligation => "Consumer Obligations",
                ObjectivePreparationClaimKindEnum.LedgerObligation => "Ledger Obligations",
                ObjectivePreparationClaimKindEnum.Uncertainty => "Remaining Uncertainty",
                ObjectivePreparationClaimKindEnum.OwnerDecision => "Recorded Owner Decisions",
                _ => "Preparation Claims"
            };
        }

        private static bool HasAnchor(ObjectivePreparationAnchor? anchor)
        {
            return anchor != null && (!String.IsNullOrWhiteSpace(anchor.VesselId)
                || !String.IsNullOrWhiteSpace(anchor.Ref)
                || !String.IsNullOrWhiteSpace(anchor.ResolvedCommit));
        }

        private static string RenderAnchor(ObjectivePreparationAnchor anchor)
        {
            List<string> parts = new List<string>();
            if (!String.IsNullOrWhiteSpace(anchor.VesselId)) parts.Add("vessel `" + BoundItem(anchor.VesselId) + "`");
            if (!String.IsNullOrWhiteSpace(anchor.Ref)) parts.Add("ref `" + BoundItem(anchor.Ref) + "`");
            if (!String.IsNullOrWhiteSpace(anchor.ResolvedCommit)) parts.Add("commit `" + BoundItem(anchor.ResolvedCommit) + "`");
            string verification = String.IsNullOrWhiteSpace(anchor.ResolvedCommit)
                ? "[unresolved revision; recheck required]"
                : "[verified revision]";
            return verification + " " + String.Join(", ", parts);
        }

        private static bool AppendTextSection(StringBuilder builder, string heading, string? text, int maxChars)
        {
            if (String.IsNullOrWhiteSpace(text)) return true;
            if (!AppendAtomic(builder, heading, maxChars, true)) return false;
            return AppendAtomic(builder, BoundItem(text), maxChars, true);
        }

        private static bool AppendListSection(StringBuilder builder, string heading, IEnumerable<string>? values, int maxChars)
        {
            List<string> items = values?
                .Where(value => !String.IsNullOrWhiteSpace(value))
                .Select(value => BoundItem(value.Trim()))
                .ToList() ?? new List<string>();
            if (items.Count == 0) return true;

            int before = builder.Length;
            if (!AppendAtomic(builder, heading, maxChars, true)) return false;
            int written = 0;
            for (int index = 0; index < items.Count; index++)
            {
                string item = items[index];
                bool hasMore = index < items.Count - 1;
                const string omitted = "- [additional items omitted]";
                int itemLimit = hasMore ? maxChars - omitted.Length - 2 : maxChars;
                if (!AppendAtomic(builder, "- " + item, itemLimit, true))
                {
                    int remaining = itemLimit - builder.Length - (builder.Length > 0 ? 2 : 0);
                    if (remaining > 40)
                    {
                        AppendAtomic(builder, "- " + BoundToLength(item, remaining - 2), itemLimit, true);
                        written++;
                    }
                    break;
                }
                written++;
            }
            if (written == 0)
            {
                builder.Length = before;
                return false;
            }
            if (written < items.Count)
            {
                AppendAtomic(builder, "- [additional items omitted]", maxChars, true);
                return false;
            }
            return true;
        }

        private static bool AppendAtomic(StringBuilder builder, string? value, int maxChars, bool blankLineBefore)
        {
            if (String.IsNullOrWhiteSpace(value)) return true;
            string prefix = blankLineBefore && builder.Length > 0 ? Environment.NewLine + Environment.NewLine : String.Empty;
            string text = value.Trim();
            if (builder.Length + prefix.Length + text.Length > maxChars) return false;
            builder.Append(prefix);
            builder.Append(text);
            return true;
        }

        private static string BoundItem(string value)
        {
            string normalized = value.Trim();
            if (normalized.Length <= _MaxItemChars) return normalized;
            return normalized.Substring(0, _MaxItemChars - 24).TrimEnd() + " … [item truncated]";
        }

        private static string BoundToLength(string value, int maxChars)
        {
            const string marker = " … [item truncated]";
            if (value.Length <= maxChars) return value;
            if (maxChars <= marker.Length) return marker.Substring(0, maxChars);
            return value.Substring(0, maxChars - marker.Length).TrimEnd() + marker;
        }

        private static string StartMarker(string objectiveId)
        {
            return "<!-- armada-objective-brief:" + objectiveId + " -->";
        }
    }
}
