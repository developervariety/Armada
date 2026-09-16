namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The repository context a prior-art search runs against: the vessel, its working checkout, the
    /// target ref the landed search reads, and the default branch. The deterministic retriever carries
    /// it unchanged to every <see cref="Armada.Core.Services.Interfaces.IPriorArtSource"/> call so the
    /// source knows which repository and revision to read. A test source ignores it; the production
    /// source resolves the git repository from it.
    /// </summary>
    public sealed class PriorArtSearchContext
    {
        /// <summary>The vessel whose repository is searched. May be empty in a unit fixture.</summary>
        public string VesselId { get; init; } = String.Empty;

        /// <summary>The repository path the landed and ref searches read, typically the working checkout.</summary>
        public string? RepoPath { get; init; }

        /// <summary>The target ref the landed search reads, typically the vessel default branch.</summary>
        public string? TargetRef { get; init; }

        /// <summary>The vessel default branch, used to exclude the landed tip from the unlanded-branch search.</summary>
        public string DefaultBranch { get; init; } = "main";
    }

    /// <summary>
    /// One prior-art search input: the objective text (and any extra text, such as a captain's stated
    /// plan or a diff's added type names) the retriever mines for search terms, plus the repository
    /// context. The retriever never trusts the caller's terms; it derives them itself, so this carries
    /// text, not a pre-built term list.
    /// </summary>
    public sealed class PriorArtQuery
    {
        /// <summary>The repository context to search.</summary>
        public required PriorArtSearchContext Context { get; init; }

        /// <summary>The objective title.</summary>
        public string Title { get; init; } = String.Empty;

        /// <summary>The objective description.</summary>
        public string Description { get; init; } = String.Empty;

        /// <summary>The objective acceptance criteria.</summary>
        public IReadOnlyList<string> AcceptanceCriteria { get; init; } = new List<string>();

        /// <summary>
        /// Extra text to mine for terms alongside the objective: a captain's stated plan for the
        /// premise tool, or the added type and method names from a diff for the Judge seam. Optional.
        /// </summary>
        public string ExtraText { get; init; } = String.Empty;

        /// <summary>The open objective, when any, whose own identifiers must be excluded from the open-objective search.</summary>
        public string? SelfObjectiveId { get; init; }
    }

    /// <summary>
    /// A raw hit from an <see cref="Armada.Core.Services.Interfaces.IPriorArtSource"/>: the surface it
    /// was found on, a <c>path:line</c> or an objective id, the search term that matched, an excerpt,
    /// and the ref or branch it lives on when the surface is not the landed tip. The retriever turns
    /// hits into de-duplicated, capped candidates.
    /// </summary>
    public sealed class PriorArtHit
    {
        /// <summary>Where the hit was found.</summary>
        public required PriorArtWhereEnum Where { get; init; }

        /// <summary>A <c>path:line</c> for a code surface, or an objective id for an open-objective hit.</summary>
        public required string Location { get; init; }

        /// <summary>The search term that matched.</summary>
        public string Term { get; init; } = String.Empty;

        /// <summary>An excerpt around the hit; the retriever caps it at forty lines.</summary>
        public string Excerpt { get; init; } = String.Empty;

        /// <summary>The branch or ref the hit lives on when the surface is not the landed tip, else null.</summary>
        public string? Ref { get; init; }
    }

    /// <summary>
    /// One prior-art candidate the model reasons over: the surface, the <c>path:line</c> (or objective
    /// id), a bounded excerpt, the branch or ref it lives on, and the terms that reached it. Every
    /// typed answer carries the candidate's location so a reader verifies the evidence rather than
    /// trusting the model.
    /// </summary>
    public sealed class PriorArtCandidate
    {
        /// <summary>Where the candidate was found.</summary>
        public required PriorArtWhereEnum Where { get; init; }

        /// <summary>A <c>path:line</c> for a code surface, or an objective id for an open-objective candidate.</summary>
        public required string Location { get; init; }

        /// <summary>The branch or ref the candidate lives on, or null for a landed candidate.</summary>
        public string? Ref { get; init; }

        /// <summary>A bounded excerpt (forty lines at most) around the candidate.</summary>
        public string Excerpt { get; init; } = String.Empty;

        /// <summary>The search terms that reached this candidate, in order of first appearance.</summary>
        public IReadOnlyList<string> Terms { get; init; } = new List<string>();

        /// <summary>The <c>where</c> label used in state and issue text.</summary>
        public string WhereLabel
        {
            get
            {
                switch (Where)
                {
                    case PriorArtWhereEnum.Landed: return "landed";
                    case PriorArtWhereEnum.UnlandedBranch: return "unlanded_branch";
                    case PriorArtWhereEnum.RecoverRef: return "recover_ref";
                    case PriorArtWhereEnum.OpenObjective: return "open_objective";
                    default: return "unknown";
                }
            }
        }
    }

    /// <summary>
    /// The deterministic retrieval result: the candidates (capped at twelve and a total token budget),
    /// the terms the retriever mined, and whether the cap truncated the candidate set. The "contextual"
    /// half of D26 <c>prior_art</c> — everything the model reasons over is here, and the model may never
    /// claim something exists that this result did not show it.
    /// </summary>
    public sealed class PriorArtRetrieval
    {
        /// <summary>The candidates, capped at twelve and a total token budget.</summary>
        public IReadOnlyList<PriorArtCandidate> Candidates { get; init; } = new List<PriorArtCandidate>();

        /// <summary>The search terms the retriever mined from the query.</summary>
        public IReadOnlyList<string> Terms { get; init; } = new List<string>();

        /// <summary>True when the candidate cap or the token budget truncated the candidate set.</summary>
        public bool Truncated { get; init; }

        /// <summary>True when the retrieval found at least one candidate.</summary>
        public bool HasCandidates => Candidates.Count > 0;

        /// <summary>An empty retrieval, for the no-term and no-hit cases.</summary>
        /// <param name="terms">The terms that were mined, for the recorded event; optional.</param>
        /// <returns>An empty retrieval.</returns>
        public static PriorArtRetrieval Empty(IReadOnlyList<string>? terms = null)
        {
            return new PriorArtRetrieval { Terms = terms ?? new List<string>() };
        }
    }
}
