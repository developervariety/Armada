namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins the deterministic, bounded objective brief that both dispatch paths deliver.
    /// </summary>
    public sealed class ObjectiveBriefRendererTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Brief Renderer";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Render includes every preparation kind in stable section order", () =>
            {
                Objective objective = FullObjective();

                string brief = ObjectiveBriefRenderer.Render(objective);

                AssertInOrder(brief,
                    "# Objective Brief",
                    "## Acceptance Criteria",
                    "## Scope",
                    "## Non-Goals",
                    "## Prepared Research",
                    "### Required Dispatch Gates",
                    "### Source and Target Anchors",
                    "### Required Sibling Inputs",
                    "### Readable Source Paths",
                    "### Dispatch Entry Points",
                    "### Implementation Types to Reuse",
                    "### Catalogue Inputs",
                    "### Provisioning Requirements",
                    "### Response Predicates and Result Rules",
                    "### Cleanup Requirements",
                    "### Consumer Obligations",
                    "### Ledger Obligations",
                    "### Remaining Uncertainty",
                    "### Recorded Owner Decisions",
                    "### Refinement Summary",
                    "### Rollout and Execution Constraints",
                    "### Evidence");
                AssertContains("Source: [verified revision] vessel `vsl_source`, ref `refs/tags/source`, commit `1111111`", brief);
                AssertContains("Target: [verified revision] vessel `vsl_target`, ref `main`, commit `2222222`", brief);
                AssertContains("[Verified] claim-SourcePath", brief);
                AssertContains("vessel `ReferenceSource` at `../ReferenceSource`; artifacts: output/extracted-artifacts", brief);
                AssertContains("Evidence: evidence-SourcePath.", brief);
                AssertContains("<!-- /armada-objective-brief -->", brief);
            }).ConfigureAwait(false);

            await RunTest("Render omits empty values and mission augmentation is idempotent", () =>
            {
                Objective objective = new Objective
                {
                    Id = "obj_brief_empty",
                    Title = "Small objective",
                    Description = "   ",
                    RefinementSummary = "\t",
                    AcceptanceCriteria = new List<string> { "", "  " },
                    NonGoals = new List<string>(),
                    RolloutConstraints = new List<string> { " " },
                    EvidenceLinks = new List<string> { "\n" },
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { Ref = " " },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            new ObjectivePreparationClaim { Text = " " }
                        }
                    }
                };

                string brief = ObjectiveBriefRenderer.Render(objective);
                string once = ObjectiveBriefRenderer.AppendToMissionDescription("Operator instruction.", objective);
                string twice = ObjectiveBriefRenderer.AppendToMissionDescription(once, objective);

                AssertFalse(brief.Contains("## Scope", StringComparison.Ordinal), "An empty scope must not emit a heading.");
                AssertFalse(brief.Contains("## Acceptance Criteria", StringComparison.Ordinal), "An empty criteria list must not emit a heading.");
                AssertFalse(brief.Contains("## Prepared Research", StringComparison.Ordinal), "Empty preparation must not emit a heading.");
                AssertEqual(once, twice, "Retrying augmentation must not add a second objective brief.");
                AssertEqual(1, CountOccurrences(twice, "<!-- armada-objective-brief:obj_brief_empty -->"),
                    "The augmented description must contain one authoritative brief marker.");
            }).ConfigureAwait(false);

            await RunTest("Render bounds long items and the complete brief", () =>
            {
                Objective objective = FullObjective();
                objective.Preparation.Claims = new List<ObjectivePreparationClaim>();
                for (int i = 0; i < 30; i++)
                {
                    objective.Preparation.Claims.Add(new ObjectivePreparationClaim
                    {
                        Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                        Text = "claim-" + i + "-" + new string('x', 1800)
                    });
                }

                string brief = ObjectiveBriefRenderer.Render(objective, 4000);

                AssertTrue(brief.Length <= 4000, "The renderer must honor the caller's complete-brief limit.");
                AssertContains("[item truncated]", brief);
                AssertContains("[additional items omitted]", brief);
                AssertContains("[additional preparation sections omitted]", brief);
                AssertContains("<!-- /armada-objective-brief -->", brief);
            }).ConfigureAwait(false);

            await RunTest("Render labels claims that need recheck with their reason", () =>
            {
                Objective objective = new Objective
                {
                    Id = "obj_brief_recheck",
                    Title = "Recheck objective",
                    Preparation = new ObjectivePreparation
                    {
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            new ObjectivePreparationClaim
                            {
                                Kind = ObjectivePreparationClaimKindEnum.DispatchEntryPoint,
                                Text = "The dispatcher enters through VoyageDispatchService.",
                                State = ObjectivePreparationClaimStateEnum.NeedsRecheck,
                                InvalidationReason = "Source anchor changed."
                            },
                            new ObjectivePreparationClaim
                            {
                                Kind = ObjectivePreparationClaimKindEnum.OwnerDecision,
                                Text = "Keep the current pipeline.",
                                State = ObjectivePreparationClaimStateEnum.Verified
                            }
                        }
                    }
                };

                string brief = ObjectiveBriefRenderer.Render(objective);

                AssertContains("[RECHECK REQUIRED: Source anchor changed.] The dispatcher enters through VoyageDispatchService.", brief);
                AssertContains("[Verified] Keep the current pipeline.", brief);
            }).ConfigureAwait(false);

            await RunTest("Large core sections cannot remove prepared research", () =>
            {
                Objective objective = FullObjective();
                objective.Description = new string('s', 12000);
                objective.AcceptanceCriteria = new List<string> { new string('a', 12000) };

                string brief = ObjectiveBriefRenderer.Render(objective, 4000);

                AssertTrue(brief.Length <= 4000, "The complete brief must stay bounded.");
                AssertContains("## Prepared Research", brief);
                AssertContains("claim-DispatchEntryPoint", brief);
                AssertContains("<!-- /armada-objective-brief -->", brief);
            }).ConfigureAwait(false);

            await RunTest("Render keeps a multi-paragraph scope whole under the default limit", () =>
            {
                Objective objective = FullObjective();
                string lastSentence = "The last paragraph names the provisioning command every stage must run.";
                objective.Description = new string('s', 2800) + " " + lastSentence;

                string brief = ObjectiveBriefRenderer.Render(objective);

                AssertContains(lastSentence, brief);
                AssertFalse(brief.Contains("[item truncated]", System.StringComparison.Ordinal),
                    "A scope under the scope bound must not be truncated.");
            }).ConfigureAwait(false);

            await RunTest("Render truncates a scope past its bound with the marker", () =>
            {
                Objective objective = FullObjective();
                objective.Description = new string('s', 7000) + " tail-that-must-not-appear";

                string brief = ObjectiveBriefRenderer.Render(objective);

                AssertContains("[item truncated]", brief);
                AssertFalse(brief.Contains("tail-that-must-not-appear", System.StringComparison.Ordinal),
                    "Text past the scope bound is not rendered.");
            }).ConfigureAwait(false);

            await RunTest("Render keeps acceptance criteria ahead of a huge scope", () =>
            {
                Objective objective = FullObjective();
                objective.Description = new string('s', 12000);
                objective.AcceptanceCriteria = new List<string> { "Gate stays green" };

                string brief = ObjectiveBriefRenderer.Render(objective, 2000);

                AssertTrue(brief.Length <= 2000, "The complete brief must stay bounded.");
                AssertContains("## Acceptance Criteria", brief);
                AssertContains("Gate stays green", brief);
                int criteriaAt = brief.IndexOf("## Acceptance Criteria", System.StringComparison.Ordinal);
                int scopeAt = brief.IndexOf("## Scope", System.StringComparison.Ordinal);
                AssertTrue(criteriaAt >= 0 && (scopeAt < 0 || criteriaAt < scopeAt),
                    "Criteria must render before Scope so a tight budget drops Scope first.");
            }).ConfigureAwait(false);

            await RunTest("An anchor without an immutable commit is labeled unresolved", () =>
            {
                Objective objective = new Objective
                {
                    Id = "obj_anchor",
                    Title = "Resolve the target",
                    Preparation = new ObjectivePreparation
                    {
                        Target = new ObjectivePreparationAnchor { VesselId = "vsl_target", Ref = "main" }
                    }
                };

                string brief = ObjectiveBriefRenderer.Render(objective);

                AssertContains("Target: [unresolved revision; recheck required] vessel `vsl_target`, ref `main`", brief);
                AssertFalse(brief.Contains("Target: [verified revision]", StringComparison.Ordinal),
                    "A mutable ref must not be presented as verified.");
            }).ConfigureAwait(false);

            await RunTest("A null stored claims collection renders safely", () =>
            {
                Objective objective = new Objective
                {
                    Id = "obj_null_claims",
                    Title = "Legacy preparation",
                    RefinementSummary = "Keep the valid summary.",
                    Preparation = new ObjectivePreparation { Claims = null! }
                };

                string brief = ObjectiveBriefRenderer.Render(objective);

                AssertContains("Keep the valid summary.", brief);
                AssertContains("<!-- /armada-objective-brief -->", brief);
            }).ConfigureAwait(false);
        }

        private static Objective FullObjective()
        {
            List<ObjectivePreparationClaim> claims = new List<ObjectivePreparationClaim>();
            foreach (ObjectivePreparationClaimKindEnum kind in Enum.GetValues<ObjectivePreparationClaimKindEnum>())
            {
                claims.Add(new ObjectivePreparationClaim
                {
                    Kind = kind,
                    Text = "claim-" + kind,
                    EvidenceLinks = new List<string> { "evidence-" + kind }
                });
            }

            return new Objective
            {
                Id = "obj_brief_full",
                Title = "Deliver prepared work",
                Description = "Change only the selected repository.",
                AcceptanceCriteria = new List<string> { "The verified behavior is present." },
                NonGoals = new List<string> { "Do not change an unrelated service." },
                RefinementSummary = "Reuse the current dispatch seam.",
                RolloutConstraints = new List<string> { "Run the focused suite first." },
                EvidenceLinks = new List<string> { "docs/evidence.md" },
                Preparation = new ObjectivePreparation
                {
                    RequiredForDispatch = true,
                    RequiredClaimKinds = new List<ObjectivePreparationClaimKindEnum> { ObjectivePreparationClaimKindEnum.SourcePath },
                    RequiredSiblingInputs = new List<ObjectivePreparationSiblingInput>
                    {
                        new ObjectivePreparationSiblingInput
                        {
                            VesselRef = "ReferenceSource",
                            RelativePath = "../ReferenceSource",
                            RequiredArtifactPaths = new List<string> { "output/extracted-artifacts" }
                        }
                    },
                    Source = new ObjectivePreparationAnchor
                    {
                        VesselId = "vsl_source",
                        Ref = "refs/tags/source",
                        ResolvedCommit = "1111111"
                    },
                    Target = new ObjectivePreparationAnchor
                    {
                        VesselId = "vsl_target",
                        Ref = "main",
                        ResolvedCommit = "2222222"
                    },
                    Claims = claims
                }
            };
        }

        private void AssertInOrder(string value, params string[] expected)
        {
            int prior = -1;
            foreach (string item in expected)
            {
                int current = value.IndexOf(item, StringComparison.Ordinal);
                AssertTrue(current > prior, "Expected section in stable order: " + item);
                prior = current;
            }
        }

        private static int CountOccurrences(string value, string search)
        {
            int count = 0;
            int offset = 0;
            while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += search.Length;
            }
            return count;
        }
    }
}
