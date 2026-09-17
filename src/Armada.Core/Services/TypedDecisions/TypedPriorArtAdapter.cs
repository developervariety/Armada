namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D26 <c>prior_art</c> adapter — "does this already exist?" asked with evidence, at the two
    /// admiral-side seams D26 wires (the dispatch preflight and the pre-Judge handoff). It is a
    /// standalone adapter, not built on the single-confidence skeleton, because it carries several
    /// independent signals: the deterministic retriever (the "contextual" half) supplies candidates, and
    /// the model answers per-candidate <c>delivers</c> plus the voyage-level <c>already_done</c>,
    /// <c>integrate_not_duplicate</c>, and <c>reimplements</c> Nouls (the "honest" half). The adapter is
    /// informative and additive only: it adds preview issues or a Judge review instruction and NEVER
    /// dismisses, deletes, approves, lands, dispatches, or writes memory. It ships in Gate, fails closed to
    /// doing nothing, records an event on every consulted path, and never throws into the caller.
    /// </summary>
    public sealed class TypedPriorArtAdapter
    {
        #region Public-Members

        /// <summary>The decision-point name in the <c>typedDecisions.decisions</c> settings map.</summary>
        public const string DecisionPoint = "prior_art";

        /// <summary>Stable code of the blocking issue added when the deliverable is already present in a candidate.</summary>
        public const string AlreadyDoneIssueCode = "objective_prior_art_found";

        /// <summary>Stable code of the advisory issue that appends candidates as landed seams to consume.</summary>
        public const string IntegrateIssueCode = "objective_prior_art_integrate";

        /// <summary>Stable code of the advisory issue that recommends a read-only PriorArtAnalyst stage before the Worker.</summary>
        public const string AnalystStageIssueCode = "prior_art_analyst_stage_recommended";

        #endregion

        #region Private-Members

        private const string _Header = "[TypedPriorArtAdapter] ";
        private const string _Area = "brief";

        // The uncertain band for the conditional read-only analyst stage: an already_done the model can
        // neither confirm nor dismiss, on an objective large enough that a wasted Worker run is
        // expensive. Below the band there is nothing to check; above it the preflight issue already
        // fires. The band and the size threshold are the D26 conditional-stage trigger.
        private const double _UncertainBandLow = 0.4;
        private const double _UncertainBandHigh = 0.7;
        private const int _LargeObjectiveCriteria = 3;
        private const int _LargeObjectiveDescriptionChars = 800;

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly IPriorArtRetriever? _Retriever;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the D26 adapter.
        /// </summary>
        /// <param name="client">Typed-decision client (the null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="retriever">The deterministic retriever; null makes every seam a no-op (nothing to reason over).</param>
        /// <param name="logging">Logging module.</param>
        public TypedPriorArtAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            IPriorArtRetriever? retriever,
            LoggingModule logging)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Retriever = retriever;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The D26 preflight seam (extends D5 Q1). Retrieves prior-art candidates for the objective and,
        /// in Gate mode at or above the decision threshold, adds an Error issue
        /// <see cref="AlreadyDoneIssueCode"/> when the deliverable is already present in a candidate, an
        /// advisory issue <see cref="IntegrateIssueCode"/> appending candidates as landed seams to
        /// consume, and, when <c>already_done</c> lands in the uncertain band on a large objective, an
        /// advisory issue <see cref="AnalystStageIssueCode"/> recommending a read-only analyst stage. The
        /// operator closes or re-scopes the row; the adapter never auto-closes and never dispatches. The
        /// deterministic preview is returned unchanged in every non-gate case. Never throws.
        /// </summary>
        /// <param name="objective">The objective being previewed.</param>
        /// <param name="vessel">The resolved target vessel.</param>
        /// <param name="result">The dispatch preview built by the deterministic block; mutated in place.</param>
        /// <param name="token">Cancellation token, forwarded so the client timeout links to the caller.</param>
        public async Task EvaluatePreflightAsync(
            Objective objective,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (objective == null || vessel == null || result == null) return;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off || _Retriever == null) return;

            PriorArtRetrieval retrieval;
            try
            {
                PriorArtQuery query = new PriorArtQuery
                {
                    Context = ContextFor(vessel),
                    Title = objective.Title ?? String.Empty,
                    Description = objective.Description ?? String.Empty,
                    AcceptanceCriteria = (objective.AcceptanceCriteria ?? new List<string>())
                        .Where(item => !String.IsNullOrWhiteSpace(item)).ToList(),
                    SelfObjectiveId = objective.Id
                };
                retrieval = await _Retriever.RetrieveAsync(query, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "preflight retrieval failed, deterministic preview stands: " + ex.Message);
                return;
            }

            // Nothing to reason over means no egress: the retrieval found no candidate, so the model is
            // never asked. This is the common case and it costs one git search, not a model call.
            if (!retrieval.HasCandidates) return;

            string deliverable = DeliverableOf(objective);
            (TypedDecisionResult? decision, PriorArtReading? reading, string redacted) =
                await ConsultAsync(deliverable, retrieval, isJudge: false, token).ConfigureAwait(false);
            if (decision == null || reading == null)
            {
                await RecordUnavailableAsync(retrieval, redacted, null, token).ConfigureAwait(false);
                return;
            }

            bool gate = cfg.Mode == TypedDecisionModeEnum.Gate;
            bool alreadyDone = reading.AlreadyDone >= cfg.GateThreshold;
            bool integrate = reading.Integrate >= cfg.GateThreshold;
            bool analystBand = !alreadyDone
                && reading.AlreadyDone >= _UncertainBandLow
                && reading.AlreadyDone <= _UncertainBandHigh
                && IsLargeObjective(objective);

            string candidateList = PriorArtDecisionShapes.RenderCandidates(retrieval);

            if (gate && alreadyDone)
            {
                AddIssue(result, AlreadyDoneIssueCode, ReadinessSeverityEnum.Error,
                    "Prior art: the objective's deliverable already appears in a candidate; close or re-scope the row before dispatch. Candidates: "
                        + candidateList,
                    candidateList);
            }

            if (gate && integrate)
            {
                AddIssue(result, IntegrateIssueCode, ReadinessSeverityEnum.Warning,
                    "Prior art: consume an existing seam rather than write a new type. Landed seams to consume: " + candidateList,
                    candidateList);
            }

            if (gate && analystBand)
            {
                AddIssue(result, AnalystStageIssueCode, ReadinessSeverityEnum.Warning,
                    "Prior art is uncertain (already_done in the uncertain band) on a large objective; consider a read-only "
                        + "PriorArtAnalyst stage before the Worker, briefed with the candidates, to settle whether the work already exists. Candidates: "
                        + candidateList,
                    candidateList);
            }

            bool applied = gate && (alreadyDone || integrate || analystBand);
            if (applied)
                await RecordGatedAsync(retrieval, redacted, decision, reading, null, token).ConfigureAwait(false);
            else
                await RecordShadowAsync(retrieval, redacted, decision, reading, cfg.Mode, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// The D26 Judge seam (extends D4, one more Noul). Retrieves prior-art candidates for the diff's
        /// added type and method names and, in Gate mode at or above threshold, returns a review
        /// instruction naming the candidates the diff appears to re-implement. A hit is a review
        /// instruction, never a verdict: the caller prepends it to the Judge's brief and the Judge still
        /// judges. Returns null when nothing gates. Never throws.
        /// </summary>
        /// <param name="mission">The finished stage whose diff is checked, for event scope.</param>
        /// <param name="vessel">The target vessel.</param>
        /// <param name="addedTypesText">The added type and method names mined from the diff.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The review instruction, or null when nothing gates.</returns>
        public async Task<string?> EvaluateJudgeInstructionAsync(
            Mission mission,
            Vessel vessel,
            string addedTypesText,
            CancellationToken token)
        {
            if (vessel == null) return null;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off || _Retriever == null) return null;
            if (String.IsNullOrWhiteSpace(addedTypesText)) return null;

            PriorArtRetrieval retrieval;
            try
            {
                PriorArtQuery query = new PriorArtQuery
                {
                    Context = ContextFor(vessel),
                    ExtraText = addedTypesText
                };
                retrieval = await _Retriever.RetrieveAsync(query, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "judge retrieval failed, no instruction added: " + ex.Message);
                return null;
            }

            if (!retrieval.HasCandidates) return null;

            (TypedDecisionResult? decision, PriorArtReading? reading, string redacted) =
                await ConsultAsync(addedTypesText, retrieval, isJudge: true, token).ConfigureAwait(false);
            if (decision == null || reading == null)
            {
                await RecordUnavailableAsync(retrieval, redacted, mission, token).ConfigureAwait(false);
                return null;
            }

            bool gate = cfg.Mode == TypedDecisionModeEnum.Gate && reading.Reimplements >= cfg.GateThreshold;
            if (gate)
            {
                await RecordGatedAsync(retrieval, redacted, decision, reading, mission, token).ConfigureAwait(false);
                return "The prior_art check reads the diff as re-implementing a capability already present in one or more candidates. "
                    + "As the Judge, verify whether the diff should consume the candidate through a seam instead, and treat a genuine "
                    + "re-implementation as a finding. Candidates: " + PriorArtDecisionShapes.RenderCandidates(retrieval);
            }

            await RecordShadowAsync(retrieval, redacted, decision, reading, cfg.Mode, mission, token).ConfigureAwait(false);
            return null;
        }

        /// <summary>
        /// Build the repository context a prior-art search runs against from a vessel: the working
        /// checkout as the repository path and the default branch as the target ref.
        /// </summary>
        /// <param name="vessel">The vessel.</param>
        /// <returns>The search context.</returns>
        public static PriorArtSearchContext ContextFor(Vessel vessel)
        {
            return new PriorArtSearchContext
            {
                VesselId = vessel?.Id ?? String.Empty,
                RepoPath = vessel?.LocalPath,
                TargetRef = vessel?.DefaultBranch,
                DefaultBranch = String.IsNullOrWhiteSpace(vessel?.DefaultBranch) ? "main" : vessel!.DefaultBranch
            };
        }

        #endregion

        #region Private-Methods

        private async Task<(TypedDecisionResult?, PriorArtReading?, string)> ConsultAsync(
            string deliverable,
            PriorArtRetrieval retrieval,
            bool isJudge,
            CancellationToken token)
        {
            string redacted;
            TypedDecisionRequest request;
            int candidateCount = retrieval.Candidates.Count;
            try
            {
                RedactedDecisionState redactedState = DecisionStateRedactor.RedactState(
                    PriorArtDecisionShapes.BuildState(deliverable, retrieval), _Settings.MaxStateChars);
                object state = redactedState.State;
                redacted = redactedState.Text;
                request = new TypedDecisionRequest
                {
                    DecisionPoint = DecisionPoint,
                    State = state,
                    Questions = isJudge
                        ? PriorArtDecisionShapes.BuildJudgeQuestions(candidateCount)
                        : PriorArtDecisionShapes.BuildPreflightQuestions(candidateCount)
                };
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "state build failed, rule stands: " + ex.Message);
                return (null, null, String.Empty);
            }

            TypedDecisionResult decision;
            try
            {
                decision = await _Client.DecideAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "client threw, rule stands: " + ex.Message);
                return (null, null, redacted);
            }

            if (decision == null || !decision.Available) return (null, null, redacted);

            PriorArtReading reading = PriorArtDecisionShapes.Interpret(decision, candidateCount);
            return (decision, reading, redacted);
        }

        private static bool IsLargeObjective(Objective objective)
        {
            int criteria = (objective.AcceptanceCriteria ?? new List<string>())
                .Count(item => !String.IsNullOrWhiteSpace(item));
            int descriptionLength = (objective.Description ?? String.Empty).Length;
            return criteria >= _LargeObjectiveCriteria || descriptionLength >= _LargeObjectiveDescriptionChars;
        }

        private static string DeliverableOf(Objective objective)
        {
            if (!String.IsNullOrWhiteSpace(objective.Description)) return objective.Description!.Trim();
            return (objective.Title ?? String.Empty).Trim();
        }

        private Task RecordUnavailableAsync(PriorArtRetrieval retrieval, string redacted, Mission? mission, CancellationToken token)
        {
            return SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                BuildContext(retrieval, redacted, TypedDecisionResult.Exception(), null, mission), token));
        }

        private Task RecordShadowAsync(PriorArtRetrieval retrieval, string redacted, TypedDecisionResult decision, PriorArtReading reading, TypedDecisionModeEnum mode, Mission? mission, CancellationToken token)
        {
            string outcome = mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
            return SafeRecordAsync(() => _Recorder.RecordShadowAsync(
                BuildContext(retrieval, redacted, decision, reading, mission), outcome, token));
        }

        private Task RecordGatedAsync(PriorArtRetrieval retrieval, string redacted, TypedDecisionResult decision, PriorArtReading reading, Mission? mission, CancellationToken token)
        {
            return SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                BuildContext(retrieval, redacted, decision, reading, mission), token));
        }

        private TypedDecisionEventContext BuildContext(PriorArtRetrieval retrieval, string redacted, TypedDecisionResult result, PriorArtReading? reading, Mission? mission)
        {
            string verdict = reading == null
                ? "no_candidates"
                : "already_done=" + reading.AlreadyDone.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + " integrate=" + reading.Integrate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + " reimplements=" + reading.Reimplements.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPoint,
                RuleVerdict = "deterministic_retrieval:" + retrieval.Candidates.Count + "_candidates",
                ModelVerdict = verdict,
                Confidence = reading?.MaxConfidence,
                Result = result,
                RedactedState = redacted ?? String.Empty,
                Mission = mission
            };
        }

        private async Task SafeRecordAsync(Func<Task<ArmadaEvent?>> record)
        {
            try
            {
                await record().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "event record failed: " + ex.Message);
            }
        }

        private static void AddIssue(
            ObjectiveDispatchPreview result,
            string code,
            ReadinessSeverityEnum severity,
            string message,
            string relatedValue)
        {
            result.Issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = code,
                Area = _Area,
                Severity = severity,
                Message = message,
                RelatedValue = relatedValue
            });
        }

        #endregion
    }
}
