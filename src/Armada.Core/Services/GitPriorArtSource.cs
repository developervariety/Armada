namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// The production <see cref="IPriorArtSource"/>: it reads the four prior-art surfaces through the
    /// git service, the branch inventory, and the objective store. It is deliberately bounded — a
    /// bounded set of terms, branches, and refs per surface — so a preflight or handoff never becomes an
    /// expensive fan-out of git calls, and every surface is guarded so one that cannot be read (a missing
    /// checkout, an unresolvable ref) yields no hits rather than throwing. The deterministic
    /// <see cref="PriorArtRetriever"/> is the tested unit; this class is the integration that feeds it in
    /// production, and the whole path is dormant until the <c>prior_art</c> decision leaves Off.
    /// </summary>
    public sealed class GitPriorArtSource : IPriorArtSource
    {
        #region Private-Members

        // Bounds that keep the fan-out cheap. The landed surface searches more terms because it is the
        // most valuable and cheapest (one revision); ref surfaces search fewer terms across more refs.
        private const int _MaxLandedTerms = 8;
        private const int _MaxRefTerms = 6;
        private const int _MaxBranches = 15;
        private const int _MaxRecoverRefs = 20;
        private const int _MaxOpenObjectives = 40;
        private const int _MaxHits = 30;
        private const int _SamplesPerSearch = 2;
        private const string _PreservedRefPrefix = "refs/armada-preserved/";
        private const string _RecoverBranchPrefix = "recover/";
        private const int _ExcerptMaxLines = 30;
        private const int _ExcerptMaxChars = 4000;

        private readonly IGitService _Git;
        private readonly IBranchInventory _Branches;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule? _Logging;
        private const string _Header = "[GitPriorArtSource] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the production prior-art source.
        /// </summary>
        /// <param name="git">Git service for tracked-content search at a revision.</param>
        /// <param name="branches">Branch inventory for enumerating branches and preserved refs.</param>
        /// <param name="database">Database driver for reading open objectives.</param>
        /// <param name="logging">Optional logging module.</param>
        public GitPriorArtSource(IGitService git, IBranchInventory branches, DatabaseDriver database, LoggingModule? logging = null)
        {
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Branches = branches ?? throw new ArgumentNullException(nameof(branches));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<IReadOnlyList<PriorArtHit>> SearchAsync(
            PriorArtSearchContext context,
            PriorArtWhereEnum where,
            IReadOnlyList<string> terms,
            CancellationToken token)
        {
            if (context == null || terms == null || terms.Count == 0) return new List<PriorArtHit>();

            try
            {
                switch (where)
                {
                    case PriorArtWhereEnum.Landed:
                        return await SearchLandedAsync(context, terms, token).ConfigureAwait(false);
                    case PriorArtWhereEnum.UnlandedBranch:
                        return await SearchUnlandedBranchesAsync(context, terms, token).ConfigureAwait(false);
                    case PriorArtWhereEnum.RecoverRef:
                        return await SearchRecoverRefsAsync(context, terms, token).ConfigureAwait(false);
                    case PriorArtWhereEnum.OpenObjective:
                        return await SearchOpenObjectivesAsync(context, terms, token).ConfigureAwait(false);
                    default:
                        return new List<PriorArtHit>();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "surface " + where + " failed, no hits: " + ex.Message);
                return new List<PriorArtHit>();
            }
        }

        #endregion

        #region Private-Methods

        private async Task<IReadOnlyList<PriorArtHit>> SearchLandedAsync(PriorArtSearchContext context, IReadOnlyList<string> terms, CancellationToken token)
        {
            List<PriorArtHit> hits = new List<PriorArtHit>();
            if (String.IsNullOrWhiteSpace(context.RepoPath) || String.IsNullOrWhiteSpace(context.TargetRef)) return hits;

            foreach (string term in terms.Take(_MaxLandedTerms))
            {
                if (hits.Count >= _MaxHits) break;
                await AddSearchHitsAsync(hits, context.RepoPath!, context.TargetRef!, term, PriorArtWhereEnum.Landed, refLabel: null, token).ConfigureAwait(false);
            }
            return hits;
        }

        private async Task<IReadOnlyList<PriorArtHit>> SearchUnlandedBranchesAsync(PriorArtSearchContext context, IReadOnlyList<string> terms, CancellationToken token)
        {
            List<PriorArtHit> hits = new List<PriorArtHit>();
            if (String.IsNullOrWhiteSpace(context.RepoPath)) return hits;

            IReadOnlyList<string> branches = await _Branches.EnumerateLocalBranchesAsync(context.RepoPath!, null, token).ConfigureAwait(false);
            int used = 0;
            foreach (string branch in branches)
            {
                if (used >= _MaxBranches || hits.Count >= _MaxHits) break;
                if (String.Equals(branch, context.DefaultBranch, StringComparison.Ordinal)) continue;
                if (branch.StartsWith(_RecoverBranchPrefix, StringComparison.Ordinal)) continue;

                // Skip a branch already merged into the target tip: its work is landed, not unlanded.
                bool? merged = String.IsNullOrWhiteSpace(context.TargetRef)
                    ? null
                    : await _Git.TryIsAncestorAsync(context.RepoPath!, branch, context.TargetRef!, token).ConfigureAwait(false);
                if (merged == true) continue;

                used++;
                foreach (string term in terms.Take(_MaxRefTerms))
                {
                    if (hits.Count >= _MaxHits) break;
                    await AddSearchHitsAsync(hits, context.RepoPath!, branch, term, PriorArtWhereEnum.UnlandedBranch, branch, token).ConfigureAwait(false);
                }
            }
            return hits;
        }

        private async Task<IReadOnlyList<PriorArtHit>> SearchRecoverRefsAsync(PriorArtSearchContext context, IReadOnlyList<string> terms, CancellationToken token)
        {
            List<PriorArtHit> hits = new List<PriorArtHit>();
            if (String.IsNullOrWhiteSpace(context.RepoPath)) return hits;

            List<string> refs = new List<string>();
            try
            {
                IReadOnlyList<GitRefTip> preserved = await _Branches.EnumerateRefTipsAsync(context.RepoPath!, _PreservedRefPrefix, token).ConfigureAwait(false);
                refs.AddRange(preserved.Select(tip => tip.RefName).Where(name => !String.IsNullOrWhiteSpace(name)));
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "preserved ref enumeration failed: " + ex.Message);
            }

            IReadOnlyList<string> recoverBranches = await _Branches.EnumerateLocalBranchesAsync(context.RepoPath!, _RecoverBranchPrefix, token).ConfigureAwait(false);
            refs.AddRange(recoverBranches);

            int used = 0;
            foreach (string reference in refs)
            {
                if (used >= _MaxRecoverRefs || hits.Count >= _MaxHits) break;
                used++;
                foreach (string term in terms.Take(_MaxRefTerms))
                {
                    if (hits.Count >= _MaxHits) break;
                    await AddSearchHitsAsync(hits, context.RepoPath!, reference, term, PriorArtWhereEnum.RecoverRef, reference, token).ConfigureAwait(false);
                }
            }
            return hits;
        }

        private async Task<IReadOnlyList<PriorArtHit>> SearchOpenObjectivesAsync(PriorArtSearchContext context, IReadOnlyList<string> terms, CancellationToken token)
        {
            List<PriorArtHit> hits = new List<PriorArtHit>();
            List<Objective> objectives = await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);

            int used = 0;
            foreach (Objective objective in objectives)
            {
                if (used >= _MaxOpenObjectives || hits.Count >= _MaxHits) break;
                if (objective == null) continue;
                if (objective.Status == ObjectiveStatusEnum.Completed || objective.Status == ObjectiveStatusEnum.Cancelled) continue;
                if (!String.IsNullOrWhiteSpace(context.VesselId)
                    && objective.VesselIds != null && objective.VesselIds.Count > 0
                    && !objective.VesselIds.Contains(context.VesselId, StringComparer.Ordinal)) continue;

                used++;
                string haystack = (objective.Title ?? String.Empty) + "\n" + (objective.Description ?? String.Empty)
                    + "\n" + String.Join("\n", objective.AcceptanceCriteria ?? new List<string>());
                foreach (string term in terms)
                {
                    if (haystack.IndexOf(term, StringComparison.Ordinal) < 0) continue;
                    hits.Add(new PriorArtHit
                    {
                        Where = PriorArtWhereEnum.OpenObjective,
                        Location = objective.Id,
                        Term = term,
                        Excerpt = (objective.Title ?? String.Empty).Trim(),
                        Ref = null
                    });
                    break;
                }
            }
            return hits;
        }

        private async Task AddSearchHitsAsync(
            List<PriorArtHit> hits,
            string repoPath,
            string revision,
            string term,
            PriorArtWhereEnum where,
            string? refLabel,
            CancellationToken token)
        {
            GitAnchorPriorArt found;
            try
            {
                found = await _Git.SearchTrackedContentOnRevisionAsync(repoPath, revision, term, _SamplesPerSearch, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "search '" + term + "' on '" + revision + "' failed: " + ex.Message);
                return;
            }

            if (!found.Found || found.SampleLocations.Count == 0) return;
            foreach (string location in found.SampleLocations)
            {
                if (String.IsNullOrWhiteSpace(location)) continue;

                // A hit on a branch or ref is not in the reader's checkout, so it carries a bounded
                // excerpt read from that ref; a landed hit is readable at its path:line directly.
                string excerpt = String.Empty;
                if (refLabel != null)
                    excerpt = await ReadExcerptAsync(repoPath, revision, location, token).ConfigureAwait(false);

                hits.Add(new PriorArtHit
                {
                    Where = where,
                    Location = location,
                    Term = term,
                    Excerpt = excerpt,
                    Ref = refLabel
                });
            }
        }

        private async Task<string> ReadExcerptAsync(string repoPath, string revision, string location, CancellationToken token)
        {
            int colon = location.LastIndexOf(':');
            if (colon <= 0 || colon == location.Length - 1) return String.Empty;
            string path = location.Substring(0, colon);
            if (!Int32.TryParse(location.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int line) || line < 1)
                return String.Empty;

            try
            {
                string? excerpt = await _Git.ReadFileExcerptOnRevisionAsync(repoPath, revision, path, line, _ExcerptMaxLines, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(excerpt)) return String.Empty;
                return excerpt.Length <= _ExcerptMaxChars ? excerpt : excerpt.Substring(0, _ExcerptMaxChars);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "excerpt for '" + location + "' on '" + revision + "' failed: " + ex.Message);
                return String.Empty;
            }
        }

        #endregion
    }
}
