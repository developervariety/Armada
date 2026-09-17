namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D6 <c>papercut_merge</c> decision adapter. At papercut-listing time it asks the typed
    /// decision client whether two papercut groups of the same vessel and category describe the same
    /// underlying issue, and — only in Gate mode, only at or above the decision threshold — folds
    /// one group into the other in the returned listing.
    ///
    /// The merge is a listing-time annotation only: the stored papercut events are never changed and
    /// no group is deleted, so turning the decision off restores the plain grouping exactly. The
    /// deterministic behaviour (no merge) is always the fallback: an Off decision, an unavailable
    /// model, and a below-threshold answer all return the input listing unchanged. The model can only
    /// add a merge; it can never split a group or remove a report.
    /// </summary>
    public sealed class PapercutMergeAdapter
    {
        #region Public-Members

        /// <summary>
        /// The decision-point name in the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public const string DecisionPoint = "papercut_merge";

        /// <summary>
        /// Event type emitted when the model proposes (Shadow) or applies (Gate) a group merge.
        /// </summary>
        public const string MergeProposedEventType = "papercut.merge_proposed";

        /// <summary>
        /// Merge candidates considered per vessel: only the largest groups, since the long tail is
        /// where a single-report group cannot usefully absorb another.
        /// </summary>
        public const int MaxGroupsPerVessel = 30;

        /// <summary>
        /// Upper bound on model calls per listing invocation. Listing is an interactive tool, so the
        /// merge pass is bounded; once the budget is spent the remaining groups pass through unmerged.
        /// </summary>
        public const int MaxComparisonsPerListing = 60;

        #endregion

        #region Private-Members

        private const string _Header = "[PapercutMergeAdapter] ";
        private const string _QuestionId = "same_issue";

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a papercut-merge adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client (null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="database">Database driver for the <c>papercut.merge_proposed</c> event.</param>
        /// <param name="logging">Logging module.</param>
        public PapercutMergeAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            DatabaseDriver database,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the listing with same-issue groups merged, when the decision is in Gate mode and the
        /// model is confident. The rule verdict — the input listing unmerged — is returned unchanged
        /// when the decision is Off, when fewer than two groups exist, or the first time the model is
        /// unavailable in this invocation. Never throws into the caller.
        /// </summary>
        /// <param name="groups">The grouped papercuts to consider. Null is treated as empty.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The merged listing, re-sorted largest first; never null.</returns>
        public async Task<List<PapercutGroup>> MergeAsync(IReadOnlyList<PapercutGroup>? groups, CancellationToken token)
        {
            List<PapercutGroup> input = groups == null ? new List<PapercutGroup>() : new List<PapercutGroup>(groups);

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off || input.Count < 2) return input;

            List<PapercutGroup> output = new List<PapercutGroup>();
            int budget = MaxComparisonsPerListing;

            // Merge candidates only within one vessel and one category; the long tail beyond the top
            // groups per vessel passes through untouched.
            foreach (IGrouping<string, PapercutGroup> vessel in input.GroupBy(g => g.VesselId ?? "-", StringComparer.Ordinal))
            {
                List<PapercutGroup> byCount = vessel.OrderByDescending(g => g.Count).ThenByDescending(g => g.LastSeenUtc).ToList();
                List<PapercutGroup> candidates = byCount.Take(MaxGroupsPerVessel).ToList();
                if (byCount.Count > MaxGroupsPerVessel)
                    output.AddRange(byCount.Skip(MaxGroupsPerVessel));

                foreach (IGrouping<PapercutCategoryEnum, PapercutGroup> category in candidates.GroupBy(g => g.Category))
                {
                    List<PapercutGroup> bucket = category.OrderByDescending(g => g.Count).ThenByDescending(g => g.LastSeenUtc).ToList();
                    if (bucket.Count < 2)
                    {
                        output.AddRange(bucket);
                        continue;
                    }

                    List<PapercutGroup> representatives = new List<PapercutGroup>();
                    foreach (PapercutGroup group in bucket)
                    {
                        bool merged = false;
                        foreach (PapercutGroup representative in representatives)
                        {
                            if (budget <= 0) break;
                            budget--;

                            PapercutMergeVerdict verdict = await DecidePairAsync(representative, group, cfg, token).ConfigureAwait(false);
                            if (!verdict.Available)
                            {
                                // The provider is unavailable for this listing; stop calling and
                                // return the plain grouping. The rule (no merge) is the fallback.
                                return input;
                            }

                            if (verdict.Merge)
                            {
                                MergeInto(representative, group);
                                merged = true;
                                break;
                            }
                        }

                        if (!merged) representatives.Add(group);
                    }

                    output.AddRange(representatives);
                }
            }

            return output
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.LastSeenUtc)
                .ToList();
        }

        #endregion

        #region Private-Methods

        private async Task<PapercutMergeVerdict> DecidePairAsync(PapercutGroup a, PapercutGroup b, ResolvedTypedDecision cfg, CancellationToken token)
        {
            // The same skeleton as TypedDecisionAdapterBase: state build and the client call never throw
            // into the listing, the caller's token is forwarded so the client links its settings timeout
            // to it, and any fault fails closed to the rule (no merge) with an unavailable event.
            object state;
            string redacted;
            TypedDecisionRequest request;
            try
            {
                RedactedDecisionState redactedState = DecisionStateRedactor.RedactState(BuildPairState(a, b), _Settings.MaxStateChars);
                state = redactedState.State;
                redacted = redactedState.Text;
                request = new TypedDecisionRequest
                {
                    DecisionPoint = DecisionPoint,
                    State = state,
                    Questions = Questions()
                };
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "pair state build failed, rule stands: " + ex.Message);
                return new PapercutMergeVerdict { Available = false, Merge = false, Confidence = 0.0 };
            }

            TypedDecisionResult? result;
            try
            {
                result = await _Client.DecideAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "client threw, rule stands: " + ex.Message);
                result = null;
            }

            if (result == null)
                result = TypedDecisionResult.Exception();

            if (!result.Available)
            {
                await _Recorder.RecordUnavailableAsync(
                    BuildContext(a, b, "no_merge", null, null, result, redacted),
                    token).ConfigureAwait(false);
                return new PapercutMergeVerdict { Available = false, Merge = false, Confidence = 0.0 };
            }

            double scalar = InterpretSameIssue(result);
            bool proposeMerge = scalar >= cfg.GateThreshold;
            bool applyMerge = cfg.Mode == TypedDecisionModeEnum.Gate && proposeMerge;
            string modelVerdict = proposeMerge ? "same_issue" : "distinct";

            if (applyMerge)
            {
                await _Recorder.RecordGatedAsync(
                    BuildContext(a, b, "no_merge", modelVerdict, scalar, result, redacted),
                    token).ConfigureAwait(false);
            }
            else
            {
                string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                await _Recorder.RecordShadowAsync(
                    BuildContext(a, b, "no_merge", modelVerdict, scalar, result, redacted),
                    outcome,
                    token).ConfigureAwait(false);
            }

            // Shadow proposes; Gate applies. Either way, a same-issue verdict emits the domain event.
            if (proposeMerge)
                await EmitMergeProposedAsync(a, b, scalar, applyMerge, token).ConfigureAwait(false);

            return new PapercutMergeVerdict { Available = true, Merge = applyMerge, Confidence = scalar };
        }

        private static object BuildPairState(PapercutGroup a, PapercutGroup b)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["vessel"] = a.VesselId,
                ["category"] = a.Category.ToString(),
                ["group_a"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["title"] = a.SampleTitle,
                    ["detail"] = a.SampleDetail
                },
                ["group_b"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["title"] = b.SampleTitle,
                    ["detail"] = b.SampleDetail
                }
            };
        }

        private static IReadOnlyDictionary<string, TypedQuestion> Questions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_QuestionId] = new NoulQuestion(
                    "Do these two reported frictions describe the same underlying issue, such that one operator triage would address both?",
                    "the same underlying issue",
                    "two different issues")
            };
        }

        private static double InterpretSameIssue(TypedDecisionResult result)
        {
            if (result.Answers == null) return 0.0;
            if (!result.Answers.TryGetValue(_QuestionId, out TypedAnswer? answer) || answer == null) return 0.0;
            // A noul answer carries its probability in Noul and no confidence; a confidence is never a
            // stand-in for the probability that the statement is true.
            if (answer.Noul.HasValue) return answer.Noul.Value;
            return 0.0;
        }

        private static TypedDecisionEventContext BuildContext(
            PapercutGroup a,
            PapercutGroup b,
            string ruleVerdict,
            string? modelVerdict,
            double? confidence,
            TypedDecisionResult result,
            string redactedState)
        {
            return new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPoint,
                RuleVerdict = ruleVerdict,
                ModelVerdict = modelVerdict,
                Confidence = confidence,
                Result = result,
                RedactedState = redactedState
            };
        }

        private static void MergeInto(PapercutGroup representative, PapercutGroup other)
        {
            representative.Count += other.Count;

            // The group aggregates do not carry the raw captain sets, so a true union is not
            // available; take the larger distinct-captain count as a conservative floor rather than
            // summing (which would double-count captains who reported both).
            if (other.DistinctCaptainCount > representative.DistinctCaptainCount)
                representative.DistinctCaptainCount = other.DistinctCaptainCount;

            if (other.HighestSeverity > representative.HighestSeverity)
                representative.HighestSeverity = other.HighestSeverity;

            if (other.FirstSeenUtc < representative.FirstSeenUtc)
                representative.FirstSeenUtc = other.FirstSeenUtc;

            // The newest report supplies the sample text, matching PapercutService.Group.
            if (other.LastSeenUtc > representative.LastSeenUtc)
            {
                representative.LastSeenUtc = other.LastSeenUtc;
                representative.SampleTitle = other.SampleTitle;
                if (!String.IsNullOrWhiteSpace(other.SampleDetail)) representative.SampleDetail = other.SampleDetail;
                if (!String.IsNullOrWhiteSpace(other.SamplePath)) representative.SamplePath = other.SamplePath;
            }

            foreach (string missionId in other.SampleMissionIds)
            {
                if (representative.SampleMissionIds.Count >= PapercutService.MaxSampleMissions) break;
                if (!representative.SampleMissionIds.Contains(missionId))
                    representative.SampleMissionIds.Add(missionId);
            }

            if (!representative.MergedGroupKeys.Contains(other.Key))
                representative.MergedGroupKeys.Add(other.Key);
            foreach (string mergedKey in other.MergedGroupKeys)
            {
                if (!representative.MergedGroupKeys.Contains(mergedKey))
                    representative.MergedGroupKeys.Add(mergedKey);
            }
        }

        private async Task EmitMergeProposedAsync(PapercutGroup a, PapercutGroup b, double scalar, bool applied, CancellationToken token)
        {
            try
            {
                string message = "merge " + (applied ? "applied" : "proposed")
                    + " vessel=" + (a.VesselId ?? "-")
                    + " category=" + a.Category
                    + " conf=" + scalar.ToString("0.00", CultureInfo.InvariantCulture);

                Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["decision"] = DecisionPoint,
                    ["vessel_id"] = a.VesselId,
                    ["category"] = a.Category.ToString(),
                    ["group_a_key"] = a.Key,
                    ["group_a_title"] = a.SampleTitle,
                    ["group_b_key"] = b.Key,
                    ["group_b_title"] = b.SampleTitle,
                    ["confidence"] = scalar,
                    ["applied"] = applied
                };

                ArmadaEvent evt = new ArmadaEvent(MergeProposedEventType, message)
                {
                    EntityType = PapercutService.EntityType,
                    VesselId = a.VesselId,
                    TenantId = Constants.DefaultTenantId,
                    Payload = JsonSerializer.Serialize(payload)
                };

                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + MergeProposedEventType + ": " + ex.Message);
            }
        }

        #endregion
    }

    /// <summary>
    /// The verdict for one candidate pair. <see cref="Merge"/> is true only when the decision is in
    /// Gate mode and the model's same-issue answer is at or above the threshold. <see cref="Available"/>
    /// is false when the model could not answer, which stops the merge pass for the whole listing.
    /// </summary>
    public sealed class PapercutMergeVerdict
    {
        /// <summary>Whether the model produced a usable answer for this pair.</summary>
        public bool Available { get; init; }

        /// <summary>Whether the two groups should be merged in the listing.</summary>
        public bool Merge { get; init; }

        /// <summary>The same-issue scalar the gate compared to the threshold.</summary>
        public double Confidence { get; init; }
    }
}
