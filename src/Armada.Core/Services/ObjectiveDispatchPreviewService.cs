namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Builds a read-only preview of the effective target, pipeline, verification, provisioning,
    /// captain, start-ref, and brief configuration for an objective dispatch.
    /// </summary>
    public sealed class ObjectiveDispatchPreviewService : IObjectiveDispatchPreviewService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly VesselReadinessService _VesselReadiness;
        private readonly IGitService _Git;
        private readonly ArmadaSettings _Settings;
        private readonly ICaptainExecutionEnvironmentProbe _ExecutionEnvironment;

        // Deterministic-fact parsing. The preflight facts read only what the description already
        // states, so the patterns match the shapes an operator writes: a recover/ ref, a full commit
        // SHA, a path:line citation, and a backticked code identifier. Facts are best-effort and
        // bounded so the preview a scheduler runs never becomes expensive.
        private const int _MaxFactTokens = 12;
        private static readonly Regex _RecoverRefPattern = new Regex(@"recover/[A-Za-z0-9._/-]+", RegexOptions.Compiled);
        private static readonly Regex _CommitShaPattern = new Regex(@"\b[0-9a-fA-F]{40}\b", RegexOptions.Compiled);
        private static readonly Regex _PathLinePattern = new Regex(@"([A-Za-z0-9_./-]+\.[A-Za-z0-9]+):\d+(?:-\d+)?", RegexOptions.Compiled);
        private static readonly Regex _BacktickIdentifierPattern = new Regex(@"`([A-Za-z_][A-Za-z0-9_.]{4,})`", RegexOptions.Compiled);

        #endregion

        #region Public-Members

        /// <summary>
        /// The D5 preflight text-half adapter, run after the deterministic preflight block to add
        /// model flags to the preview. Null leaves the preview fully deterministic, which is the
        /// operationally-off state; it is set after construction only when the live typed-decision
        /// client exists, so every construction site and test is unchanged by default.
        /// </summary>
        public PreflightTextAdapter? PreflightAdapter { get; set; }

        /// <summary>
        /// The D19 <c>stage_necessity</c> adapter, run after the deterministic pipeline resolution to
        /// list <c>stage_optional</c> warnings for non-Judge stages the model proposes the objective
        /// does not need. Null leaves the preview listing every stage, which is the operationally-off
        /// state; it is set after construction only when the live typed-decision client exists, so every
        /// construction site and test is unchanged by default. The adapter only ever ADDS warnings; it
        /// never removes a stage, and it never proposes the Judge.
        /// </summary>
        public TypedStageNecessityAdapter? StageNecessityAdapter { get; set; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the objective dispatch preview service.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="workflowProfiles">Workflow profile resolver.</param>
        /// <param name="vesselReadiness">Vessel and workflow readiness service.</param>
        /// <param name="git">Git service used only for revision resolution.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="executionEnvironment">Read-only probe of the captain execution environment; null probes the local environment captains launch in.</param>
        public ObjectiveDispatchPreviewService(
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            VesselReadinessService vesselReadiness,
            IGitService git,
            ArmadaSettings settings,
            ICaptainExecutionEnvironmentProbe? executionEnvironment = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _VesselReadiness = vesselReadiness ?? throw new ArgumentNullException(nameof(vesselReadiness));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ExecutionEnvironment = executionEnvironment ?? new LocalCaptainExecutionEnvironmentProbe(settings);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Preview one objective without creating a voyage, mission, branch, dock, Check, or lease.
        /// Optional dispatch values let operator paths preview their effective request while the
        /// autonomous path uses the objective's own values.
        /// </summary>
        /// <param name="auth">Authenticated caller context.</param>
        /// <param name="objective">Objective to preview.</param>
        /// <param name="requestedVesselId">Optional operator-selected target vessel.</param>
        /// <param name="requestedPipelineId">Optional operator-selected pipeline ID or name.</param>
        /// <param name="captainAssignments">Optional operator-selected persona assignments.</param>
        /// <param name="missionDescriptions">Optional operator mission descriptions with effective mode and start-ref values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Complete dispatch preview.</returns>
        public async Task<ObjectiveDispatchPreview> PreviewAsync(
            AuthContext auth,
            Objective objective,
            string? requestedVesselId = null,
            string? requestedPipelineId = null,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments = null,
            IReadOnlyList<MissionDescription>? missionDescriptions = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (objective == null) throw new ArgumentNullException(nameof(objective));

            ObjectiveDispatchPreview result = new ObjectiveDispatchPreview
            {
                ObjectiveId = objective.Id,
                DeliverableMode = MissionModes.FromObjectiveKind(objective.Kind) ?? MissionModeEnum.Implementation.ToString(),
                StartFromRef = NormalizeEmpty(objective.StartFromRef),
                RenderedBrief = ObjectiveBriefRenderer.Render(objective)
            };

            EvaluateBrief(objective, result);
            EvaluatePreflightAnswers(objective, result);
            List<Objective> objectiveSnapshot = await ReadObjectivesAsync(auth, token).ConfigureAwait(false);
            ObjectiveDependencyAnalysis dependencies = ObjectiveDependencyAnalyzer.Analyze(objective, objectiveSnapshot);
            result.DependencyAnalysis = dependencies;
            result.BlockingChains = dependencies.BlockingChains
                .Select(chain => chain.ObjectiveIds.ToList())
                .ToList();
            if (dependencies.HasCycle)
            {
                AddIssue(result, "objective_dependency_cycle", "admission", ReadinessSeverityEnum.Error,
                    "The objective dependency graph contains a cycle: " + String.Join(" -> ", dependencies.CyclePath) + ".",
                    objective.Id);
            }
            else if (dependencies.BlockingNodes.Any(node => node.IsMissing))
            {
                AddIssue(result, "objective_dependency_missing", "admission", ReadinessSeverityEnum.Error,
                    "One or more objective dependencies do not exist or are not accessible.", objective.Id);
            }
            else if (!dependencies.IsDependencyReady)
            {
                AddIssue(result, "objective_dependencies_incomplete", "admission", ReadinessSeverityEnum.Error,
                    "One or more objective dependency chains are not complete.", objective.Id);
            }

            string? vesselId = ResolveTargetVesselId(objective, requestedVesselId, result);
            result.VesselId = vesselId;
            if (vesselId == null)
            {
                FinalizeResult(result);
                return result;
            }

            Vessel? vessel = await ReadVesselAsync(auth, vesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                AddIssue(result, "target_vessel_not_found", "target", ReadinessSeverityEnum.Error,
                    "The target vessel does not exist or is not accessible.", vesselId);
                FinalizeResult(result);
                return result;
            }

            await EvaluateRepositoryContextAsync(vessel, result, token).ConfigureAwait(false);
            await EvaluateStartRefAsync(vessel, result, token).ConfigureAwait(false);
            await EvaluatePreflightFactsAsync(auth, objective, vessel, result, token).ConfigureAwait(false);
            List<string> effectiveMissionModes = await EvaluateMissionDescriptionsAsync(
                vessel, objective, missionDescriptions, result, token).ConfigureAwait(false);
            await EvaluatePreparationAnchorsAsync(auth, objective, result, token).ConfigureAwait(false);
            await EvaluateSiblingProvisioningAsync(auth, objective, vessel, result, token).ConfigureAwait(false);
            EvaluateExecutionRequirements(objective, _ExecutionEnvironment, result);

            string? effectivePipelineRequest = NormalizeEmpty(requestedPipelineId)
                ?? NormalizeEmpty(objective.SuggestedPipelineId);
            Pipeline? pipeline = await ResolvePipelineReadOnlyAsync(
                vessel,
                effectivePipelineRequest,
                effectiveMissionModes.Count > 0
                    ? effectiveMissionModes.All(IsReadOnlyMissionMode)
                    : objective.Kind == ObjectiveKindEnum.Research,
                result,
                token).ConfigureAwait(false);
            if (pipeline != null)
            {
                result.PipelineId = pipeline.Id;
                result.PipelineName = pipeline.Name;
            }
            else if (!result.Issues.Any(issue => issue.Area == "pipeline" && issue.Severity == ReadinessSeverityEnum.Error))
            {
                result.PipelineName = "WorkerOnly";
            }

            List<Captain> captains = await ReadCaptainsAsync(auth, token).ConfigureAwait(false);
            EvaluateCaptainCoverage(pipeline, captains, captainAssignments, missionDescriptions, result);
            await EvaluateChecksAsync(auth, vessel, result, token).ConfigureAwait(false);

            // D5 preflight text half. Runs LAST, after the deterministic block computed the facts and
            // resolved the pipeline, so its state carries both. The model only adds issues; the
            // deterministic issues already in the preview stand regardless of what the model returns.
            if (PreflightAdapter != null)
                await PreflightAdapter.EvaluateAsync(objective, vessel, pipeline, result, token).ConfigureAwait(false);

            // D19 stage necessity. Runs on the resolved pipeline stages and lists a stage_optional
            // warning per non-Judge stage the model proposes the objective does not need. It only adds
            // warnings the operator confirms; the deterministic pipeline (every stage) stands. The Judge
            // is never proposed.
            if (StageNecessityAdapter != null)
                await RefineStageNecessityAsync(objective, vessel, pipeline, result, token).ConfigureAwait(false);

            FinalizeResult(result);
            return result;
        }

        #endregion

        #region Private-Methods

        private static void EvaluateBrief(Objective objective, ObjectiveDispatchPreview result)
        {
            if (String.IsNullOrWhiteSpace(objective.Description))
                AddIssue(result, "brief_scope_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective description must state the dispatch scope.", null);

            if (objective.AcceptanceCriteria == null || !objective.AcceptanceCriteria.Any(item => !String.IsNullOrWhiteSpace(item)))
                AddIssue(result, "brief_acceptance_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective must state at least one acceptance criterion.", null);

            ObjectivePreparation preparation = objective.Preparation ?? new ObjectivePreparation();
            List<ObjectivePreparationClaim> claims = preparation.Claims ?? new List<ObjectivePreparationClaim>();
            if (preparation.RequiredForDispatch)
            {
                if (preparation.Source == null)
                    AddIssue(result, "preparation_source_required", "brief", ReadinessSeverityEnum.Error,
                        "Required preparation must include an immutable source anchor.", null);
                if (preparation.Target == null)
                    AddIssue(result, "preparation_target_required", "brief", ReadinessSeverityEnum.Error,
                        "Required preparation must include an immutable target anchor.", null);

                if (preparation.RequiredClaimKinds == null || preparation.RequiredClaimKinds.Count == 0)
                    AddIssue(result, "preparation_required_kinds_missing", "brief", ReadinessSeverityEnum.Error,
                        "Required preparation must name at least one required claim kind.", null);

                foreach (ObjectivePreparationClaimKindEnum kind in preparation.RequiredClaimKinds ?? new List<ObjectivePreparationClaimKindEnum>())
                {
                    bool present = claims.Any(claim => claim != null
                        && claim.Kind == kind
                        && claim.State == ObjectivePreparationClaimStateEnum.Verified
                        && claim.VerifiedUtc.HasValue
                        && claim.EvidenceLinks != null
                        && claim.EvidenceLinks.Any(link => !String.IsNullOrWhiteSpace(link)));
                    if (!present)
                        AddIssue(result, "preparation_required_kind_missing", "brief", ReadinessSeverityEnum.Error,
                            "Required preparation has no current evidence-backed " + kind + " claim.", kind.ToString());
                }

                foreach (ObjectivePreparationClaim claim in claims.Where(item => item != null))
                {
                    if (!claim.VerifiedUtc.HasValue)
                        AddIssue(result, "preparation_claim_verification_time_missing", "brief", ReadinessSeverityEnum.Error,
                            "A required preparation claim has no verification time: " + claim.Text, claim.Id);
                    if (claim.EvidenceLinks == null || !claim.EvidenceLinks.Any(link => !String.IsNullOrWhiteSpace(link)))
                        AddIssue(result, "preparation_claim_evidence_missing", "brief", ReadinessSeverityEnum.Error,
                            "A required preparation claim has no evidence: " + claim.Text, claim.Id);
                }
            }
            bool hasMethod = !String.IsNullOrWhiteSpace(objective.RefinementSummary)
                || claims.Any(claim => claim != null
                    && claim.State == ObjectivePreparationClaimStateEnum.Verified
                    && !String.IsNullOrWhiteSpace(claim.Text)
                    && (claim.Kind == ObjectivePreparationClaimKindEnum.DispatchEntryPoint
                        || claim.Kind == ObjectivePreparationClaimKindEnum.ReuseType
                        || claim.Kind == ObjectivePreparationClaimKindEnum.ResponseRule));
            if (!hasMethod)
                AddIssue(result, "brief_method_missing", "brief", ReadinessSeverityEnum.Error,
                    "The objective must state an implementation method in its refinement summary or verified preparation.", null);

            foreach (ObjectivePreparationClaim claim in claims.Where(item => item != null
                && item.State == ObjectivePreparationClaimStateEnum.NeedsRecheck))
            {
                AddIssue(result, "preparation_claim_needs_recheck", "brief", ReadinessSeverityEnum.Error,
                    "A preparation claim must be verified again before dispatch: " + claim.Text, claim.Id);
            }
        }

        /// <summary>
        /// Apply the dispatch-preflight rule to the objective's recorded answers. Every question must
        /// be answered in a way that admits dispatch; an objective with no recorded answers has every
        /// question unanswered and is refused. The blocking rule itself lives in one place
        /// (<see cref="ObjectivePreflightEvaluator"/>) so this gate, the scheduler, and operator dispatch
        /// cannot disagree.
        /// </summary>
        private static void EvaluatePreflightAnswers(Objective objective, ObjectiveDispatchPreview result)
        {
            ObjectivePreflight preflight = objective.Preparation?.Preflight ?? new ObjectivePreflight();
            IReadOnlyList<int> blocking = ObjectivePreflightEvaluator.BlockingQuestions(preflight);
            result.Preflight.IncompleteQuestions = blocking.ToList();
            result.Preflight.IsComplete = blocking.Count == 0;
            if (blocking.Count > 0)
            {
                string numbers = String.Join(", ", blocking);
                AddIssue(result, ObjectivePreflightGate.IssueCode, "preflight", ReadinessSeverityEnum.Error,
                    "The dispatch preflight is incomplete; record an admitting answer for question(s) " + numbers + ".",
                    numbers);
            }
        }

        /// <summary>
        /// Compute the deterministic preflight facts the code can settle on its own and attach them to
        /// the preview. Each fact is informational: it lets an operator check a recorded answer against
        /// the repository and never blocks dispatch by itself. A fact the repository cannot settle is
        /// reported Unknown rather than guessed, and a git error never fails the preview.
        /// </summary>
        private async Task EvaluatePreflightFactsAsync(
            AuthContext auth,
            Objective objective,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            ObjectivePreflight preflight = objective.Preparation?.Preflight ?? new ObjectivePreflight();
            string repositoryPath = RepositoryPath(vessel);
            string targetRevision = !String.IsNullOrWhiteSpace(result.ResolvedStartCommit)
                ? result.ResolvedStartCommit!
                : "HEAD";
            string description = objective.Description ?? String.Empty;

            result.Preflight.Facts.Add(BuildVesselCountFact(objective, preflight));
            result.Preflight.Facts.Add(BuildDeliverableKindFact(objective, preflight, description));
            result.Preflight.Facts.Add(await BuildRecoverRefFact(objective, preflight, repositoryPath, description, token).ConfigureAwait(false));
            result.Preflight.Facts.Add(await BuildSiblingTipFact(auth, vessel, preflight, description, token).ConfigureAwait(false));
            result.Preflight.Facts.Add(await BuildCitationResolvesFact(preflight, repositoryPath, targetRevision, description, token).ConfigureAwait(false));
        }

        private static ObjectiveDispatchPreflightFact BuildVesselCountFact(Objective objective, ObjectivePreflight preflight)
        {
            const int question = 3;
            int count = (objective.VesselIds ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                Status = count == 1 ? PreflightFactStatusEnum.Pass : PreflightFactStatusEnum.Fail,
                Detail = "The objective names " + count + " target vessel(s); dispatch needs exactly one.",
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };
        }

        private static ObjectiveDispatchPreflightFact BuildDeliverableKindFact(
            Objective objective, ObjectivePreflight preflight, string description)
        {
            const int question = 2;
            string lower = description.ToLowerInvariant();
            string[] reportWords = { "report-only", "report only", "a report", "produce a report", "findings report", "report to the owner" };
            string[] committedWords = { "commit a", "commit the", "committed doc", "committed document", "census", "ledger row", "discoveries.d", "write a file", "record a file" };
            bool report = reportWords.Any(word => lower.Contains(word));
            bool committed = committedWords.Any(word => lower.Contains(word));

            PreflightFactStatusEnum status;
            string detail;
            if (report && !committed)
            {
                status = objective.Kind == ObjectiveKindEnum.Research ? PreflightFactStatusEnum.Pass : PreflightFactStatusEnum.Fail;
                detail = "The description reads report-only; a report deliverable is Kind Research (Kind is " + objective.Kind + ").";
            }
            else if (committed && !report)
            {
                status = objective.Kind == ObjectiveKindEnum.Research ? PreflightFactStatusEnum.Fail : PreflightFactStatusEnum.Pass;
                detail = "The description commits a document; a committed deliverable must not be Kind Research (Kind is " + objective.Kind + ").";
            }
            else
            {
                status = PreflightFactStatusEnum.Unknown;
                detail = "The description does not clearly indicate report-only or committed-document delivery (Kind is " + objective.Kind + ").";
            }

            return new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                Status = status,
                Detail = detail,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };
        }

        private async Task<ObjectiveDispatchPreflightFact> BuildRecoverRefFact(
            Objective objective, ObjectivePreflight preflight, string repositoryPath, string description, CancellationToken token)
        {
            const int question = 10;
            ObjectiveDispatchPreflightFact fact = new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };

            List<string> refs = MatchAll(description, _RecoverRefPattern).Distinct(StringComparer.Ordinal).ToList();
            if (refs.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The description names no recover/ ref.";
                return fact;
            }

            string rowText = description + " " + String.Join(" ", objective.EvidenceLinks ?? new List<string>());
            bool hasRecordedSha = _CommitShaPattern.IsMatch(rowText);
            List<string> unresolved = new List<string>();
            try
            {
                foreach (string reference in refs.Take(_MaxFactTokens))
                {
                    string? sha = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(repositoryPath, reference, token).ConfigureAwait(false));
                    if (sha == null) unresolved.Add(reference);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The recover/ refs could not be resolved: " + ex.Message;
                return fact;
            }

            if (unresolved.Count == 0 && hasRecordedSha)
            {
                fact.Status = PreflightFactStatusEnum.Pass;
                fact.Detail = "Every named recover/ ref resolves and a 40-character SHA is recorded on the row.";
            }
            else
            {
                fact.Status = PreflightFactStatusEnum.Fail;
                fact.Detail = unresolved.Count > 0
                    ? "These recover/ refs do not resolve in the vessel repository: " + String.Join(", ", unresolved) + "."
                    : "No 40-character SHA is recorded on the row for the named recover/ ref(s).";
            }
            return fact;
        }

        private async Task<ObjectiveDispatchPreflightFact> BuildSiblingTipFact(
            AuthContext auth, Vessel vessel, ObjectivePreflight preflight, string description, CancellationToken token)
        {
            const int question = 11;
            ObjectiveDispatchPreflightFact fact = new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };

            List<SiblingRepo> siblings = (vessel.GetSiblingRepos() ?? new List<SiblingRepo>())
                .Where(sibling => sibling != null && !String.IsNullOrWhiteSpace(sibling.VesselRef))
                .ToList();
            List<string> citedCommits = MatchAll(description, _CommitShaPattern).Distinct(StringComparer.OrdinalIgnoreCase).Take(_MaxFactTokens).ToList();
            if (siblings.Count == 0 || citedCommits.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "There is no declared sibling and cited commit to compare.";
                return fact;
            }

            bool anyFail = false;
            bool anyUnknown = false;
            try
            {
                foreach (SiblingRepo sibling in siblings)
                {
                    Vessel? siblingVessel = await ResolveVesselReferenceAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                    if (siblingVessel == null) { anyUnknown = true; continue; }
                    string siblingRepo = RepositoryPath(siblingVessel);
                    string? siblingTip = NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(siblingRepo, "HEAD", token).ConfigureAwait(false));
                    if (siblingTip == null) { anyUnknown = true; continue; }
                    foreach (string commit in citedCommits)
                    {
                        bool? ancestor = await _Git.TryIsAncestorAsync(siblingRepo, commit, siblingTip, token).ConfigureAwait(false);
                        if (ancestor == false) anyFail = true;
                        else if (ancestor == null) anyUnknown = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The sibling tips could not be compared: " + ex.Message;
                return fact;
            }

            if (anyFail)
            {
                fact.Status = PreflightFactStatusEnum.Fail;
                fact.Detail = "A declared sibling tip is behind a commit the description cites (dock.sibling_stale).";
            }
            else if (anyUnknown)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The sibling tip ancestry could not be established for every cited commit.";
            }
            else
            {
                fact.Status = PreflightFactStatusEnum.Pass;
                fact.Detail = "Every declared sibling tip is at or after each commit the description cites.";
            }
            return fact;
        }

        private async Task<ObjectiveDispatchPreflightFact> BuildCitationResolvesFact(
            ObjectivePreflight preflight, string repositoryPath, string targetRevision, string description, CancellationToken token)
        {
            const int question = 1;
            ObjectiveDispatchPreflightFact fact = new ObjectiveDispatchPreflightFact
            {
                QuestionNumber = question,
                RecordedAnswer = ObjectivePreflightEvaluator.RecordedAnswer(preflight, question)
            };

            List<string> paths = MatchAll(description, _PathLinePattern).Distinct(StringComparer.OrdinalIgnoreCase).Take(_MaxFactTokens).ToList();
            List<string> identifiers = MatchAll(description, _BacktickIdentifierPattern).Distinct(StringComparer.Ordinal).Take(_MaxFactTokens).ToList();
            if (paths.Count == 0 && identifiers.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The description cites no path:line or backticked identifier to resolve.";
                return fact;
            }

            List<string> unresolved = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    bool exists = await _Git.PathExistsOnRevisionAsync(repositoryPath, targetRevision, path, token).ConfigureAwait(false);
                    if (!exists)
                    {
                        string? suffix = await _Git.ResolveTrackedPathSuffixAsync(repositoryPath, targetRevision, path, token).ConfigureAwait(false);
                        if (String.IsNullOrWhiteSpace(suffix)) unresolved.Add(path);
                    }
                }
                foreach (string identifier in identifiers)
                {
                    GitAnchorPriorArt search = await _Git.SearchTrackedContentOnRevisionAsync(
                        repositoryPath, targetRevision, identifier, 1, token).ConfigureAwait(false);
                    if (!search.Found) unresolved.Add(identifier);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fact.Status = PreflightFactStatusEnum.Unknown;
                fact.Detail = "The cited references could not be resolved at the target tip: " + ex.Message;
                return fact;
            }

            if (unresolved.Count == 0)
            {
                fact.Status = PreflightFactStatusEnum.Pass;
                fact.Detail = "Every cited path:line and backticked identifier resolves at the target tip.";
            }
            else
            {
                fact.Status = PreflightFactStatusEnum.Fail;
                fact.Detail = "These citations do not resolve at the target tip: " + String.Join(", ", unresolved) + ".";
            }
            return fact;
        }

        private static List<string> MatchAll(string input, Regex pattern)
        {
            List<string> matches = new List<string>();
            foreach (Match match in pattern.Matches(input))
            {
                matches.Add(match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value);
            }
            return matches;
        }

        private static string? ResolveTargetVesselId(
            Objective objective,
            string? requestedVesselId,
            ObjectiveDispatchPreview result)
        {
            string? requested = NormalizeEmpty(requestedVesselId);
            List<string> objectiveTargets = (objective.VesselIds ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested != null)
            {
                if (objectiveTargets.Count != 1)
                {
                    AddIssue(result, "target_vessel_count", "target", ReadinessSeverityEnum.Error,
                        "Objective dispatch requires exactly one objective target vessel; the objective has " + objectiveTargets.Count + ".", null);
                }
                else if (!objectiveTargets.Contains(requested, StringComparer.OrdinalIgnoreCase))
                {
                    AddIssue(result, "objective_vessel_mismatch", "target", ReadinessSeverityEnum.Error,
                        "The requested vessel is not one of the objective target vessels.", requested);
                }
                return requested;
            }

            if (objectiveTargets.Count != 1)
            {
                AddIssue(result, "target_vessel_count", "target", ReadinessSeverityEnum.Error,
                    "Objective dispatch requires exactly one target vessel; the objective has " + objectiveTargets.Count + ".", null);
                return null;
            }

            return objectiveTargets[0];
        }

        private async Task EvaluateRepositoryContextAsync(
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            string repositoryPath = RepositoryPath(vessel);
            bool localExists = await _Git.IsRepositoryAsync(repositoryPath, token).ConfigureAwait(false);
            bool canClone = !String.IsNullOrWhiteSpace(vessel.RepoUrl);
            if (!localExists && !canClone)
            {
                AddIssue(result, "target_repository_unavailable", "provisioning", ReadinessSeverityEnum.Error,
                    "The target vessel has no usable local repository and no repository URL.", repositoryPath);
            }
            else if (!localExists)
            {
                AddIssue(result, "target_repository_clone_required", "provisioning", ReadinessSeverityEnum.Warning,
                    "Dock provisioning must clone the target repository before work starts.", vessel.RepoUrl);
            }
        }

        private async Task EvaluateStartRefAsync(Vessel vessel, ObjectiveDispatchPreview result, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(result.StartFromRef)) return;
            string repositoryPath = RepositoryPath(vessel);
            string? commit = await _Git.GetRevisionCommitShaAsync(repositoryPath, result.StartFromRef, token).ConfigureAwait(false);
            result.ResolvedStartCommit = NormalizeEmpty(commit);
            if (String.IsNullOrWhiteSpace(commit))
            {
                AddIssue(result, "start_from_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The objective start ref does not resolve in the target repository.", result.StartFromRef);
            }
        }

        private async Task<List<string>> EvaluateMissionDescriptionsAsync(
            Vessel vessel,
            Objective objective,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            List<string> modes = new List<string>();
            if (missionDescriptions == null || missionDescriptions.Count == 0) return modes;

            string? objectiveMode = MissionModes.FromObjectiveKind(objective.Kind);
            for (int index = 0; index < missionDescriptions.Count; index++)
            {
                MissionDescription? mission = missionDescriptions[index];
                if (mission == null) continue;
                string? suppliedMode = NormalizeEmpty(mission.Mode);
                if (suppliedMode != null && !MissionModes.IsKnown(suppliedMode))
                {
                    AddIssue(result, "invalid_mission_mode", "brief", ReadinessSeverityEnum.Error,
                        "Mission " + (index + 1) + " has an unknown mode: " + suppliedMode + ".", suppliedMode);
                }
                string effectiveMode = suppliedMode != null && MissionModes.IsKnown(suppliedMode)
                    ? MissionModes.Parse(suppliedMode).ToString()
                    : objectiveMode ?? MissionModeEnum.Implementation.ToString();
                modes.Add(effectiveMode);

                string? startRef = NormalizeEmpty(mission.StartFromRef) ?? NormalizeEmpty(objective.StartFromRef);
                if (!String.IsNullOrWhiteSpace(mission.DependsOnMissionId)
                    || !String.IsNullOrWhiteSpace(mission.DependsOnMissionAlias))
                    startRef = null;
                string? commit = startRef == null
                    ? null
                    : NormalizeEmpty(await _Git.GetRevisionCommitShaAsync(RepositoryPath(vessel), startRef, token).ConfigureAwait(false));
                result.MissionStartRefs.Add(new ObjectiveDispatchMissionRef
                {
                    MissionIndex = index,
                    Title = mission.Title ?? String.Empty,
                    Mode = effectiveMode,
                    StartFromRef = startRef,
                    ResolvedCommit = commit
                });
                if (startRef != null && commit == null)
                {
                    AddIssue(result, "mission_start_from_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                        "The start ref for mission " + (index + 1) + " does not resolve in the target repository.", startRef);
                }
            }
            result.EffectiveMissionModes = modes.ToList();
            if (modes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                result.DeliverableMode = modes[0];
            else if (modes.Count > 1)
                result.DeliverableMode = "Mixed";
            return modes;
        }

        private async Task EvaluatePreparationAnchorsAsync(
            AuthContext auth,
            Objective objective,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            ObjectivePreparation preparation = objective.Preparation ?? new ObjectivePreparation();
            await EvaluateAnchorAsync(auth, preparation.Source, "source", result, token).ConfigureAwait(false);
            await EvaluateAnchorAsync(auth, preparation.Target, "target", result, token).ConfigureAwait(false);

            if (preparation.Target != null
                && !String.IsNullOrWhiteSpace(preparation.Target.VesselId)
                && !String.Equals(preparation.Target.VesselId, result.VesselId, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(result, "preparation_target_vessel_mismatch", "target", ReadinessSeverityEnum.Error,
                    "The prepared target vessel does not match the dispatch target vessel.", preparation.Target.VesselId);
            }
        }

        private async Task EvaluateAnchorAsync(
            AuthContext auth,
            ObjectivePreparationAnchor? anchor,
            string label,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (anchor == null) return;
            if (String.IsNullOrWhiteSpace(anchor.VesselId))
            {
                AddIssue(result, "preparation_" + label + "_vessel_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " anchor must name a vessel.", null);
                return;
            }
            if (String.IsNullOrWhiteSpace(anchor.Ref) || String.IsNullOrWhiteSpace(anchor.ResolvedCommit))
            {
                AddIssue(result, "preparation_" + label + "_revision_incomplete", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " anchor must include a ref and resolved commit.", anchor.VesselId);
                return;
            }

            Vessel? anchorVessel = await ReadVesselAsync(auth, anchor.VesselId, token).ConfigureAwait(false);
            if (anchorVessel == null)
            {
                AddIssue(result, "preparation_" + label + "_vessel_not_found", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " vessel does not exist or is not accessible.", anchor.VesselId);
                return;
            }

            string? current = await _Git.GetRevisionCommitShaAsync(RepositoryPath(anchorVessel), anchor.Ref, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(current))
            {
                AddIssue(result, "preparation_" + label + "_ref_missing", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " ref does not resolve.", anchor.Ref);
            }
            else if (!RevisionMatches(current, anchor.ResolvedCommit))
            {
                AddIssue(result, "preparation_" + label + "_revision_changed", "provisioning", ReadinessSeverityEnum.Error,
                    "The prepared " + label + " ref no longer resolves to the recorded commit.", anchor.Ref);
            }
        }

        private async Task EvaluateSiblingProvisioningAsync(
            AuthContext auth,
            Objective objective,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            List<SiblingRepo> declaredSiblings = vessel.GetSiblingRepos();
            foreach (ObjectivePreparationSiblingInput required in objective.Preparation?.RequiredSiblingInputs
                ?? new List<ObjectivePreparationSiblingInput>())
            {
                Vessel? requiredVessel = await ResolveVesselReferenceAsync(auth, required.VesselRef, token).ConfigureAwait(false);
                if (requiredVessel == null)
                {
                    AddIssue(result, "required_sibling_vessel_not_found", "provisioning", ReadinessSeverityEnum.Error,
                        "The required sibling vessel does not exist or is not accessible.", required.VesselRef);
                    continue;
                }

                SiblingRepo? declaration = null;
                foreach (SiblingRepo sibling in declaredSiblings.Where(item => item != null
                    && String.Equals(NormalizeRelativePath(item.RelativePath), NormalizeRelativePath(required.RelativePath), StringComparison.OrdinalIgnoreCase)))
                {
                    if (String.Equals(sibling.VesselRef, required.VesselRef, StringComparison.OrdinalIgnoreCase))
                    {
                        declaration = sibling;
                        break;
                    }
                    Vessel? declaredVessel = await ResolveVesselReferenceAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                    if (String.Equals(declaredVessel?.Id, requiredVessel.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        declaration = sibling;
                        break;
                    }
                }
                if (declaration == null)
                {
                    AddIssue(result, "required_sibling_not_declared", "provisioning", ReadinessSeverityEnum.Error,
                        "The target vessel does not declare required sibling " + required.VesselRef + " at " + required.RelativePath + ".",
                        required.VesselRef);
                    continue;
                }

                HashSet<string> declaredArtifacts = new HashSet<string>(
                    (declaration.ExtractionArtifactPaths ?? new List<string>()).Select(NormalizeRelativePath),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string artifactPath in required.RequiredArtifactPaths ?? new List<string>())
                {
                    if (!declaredArtifacts.Contains(NormalizeRelativePath(artifactPath)))
                        AddIssue(result, "required_sibling_artifact_not_declared", "provisioning", ReadinessSeverityEnum.Error,
                            "The sibling declaration does not provision required artifact path " + artifactPath + ".",
                            required.VesselRef);
                }
            }

            foreach (SiblingRepo sibling in declaredSiblings)
            {
                if (sibling == null) continue;
                if (String.IsNullOrWhiteSpace(sibling.RelativePath))
                {
                    AddIssue(result, "sibling_relative_path_missing", "provisioning", ReadinessSeverityEnum.Error,
                        "A declared sibling repository has no relative checkout path.", null);
                    continue;
                }

                Vessel? siblingVessel = null;
                if (!String.IsNullOrWhiteSpace(sibling.VesselRef))
                {
                    siblingVessel = await ReadVesselAsync(auth, sibling.VesselRef, token).ConfigureAwait(false)
                        ?? await ReadVesselByNameAsync(auth, sibling.VesselRef, token).ConfigureAwait(false);
                }
                bool registeredSourceReady = siblingVessel != null
                    && await _Git.IsRepositoryAsync(RepositoryPath(siblingVessel), token).ConfigureAwait(false);
                bool hasSource = registeredSourceReady
                    || !String.IsNullOrWhiteSpace(siblingVessel?.RepoUrl)
                    || !String.IsNullOrWhiteSpace(sibling.RepoUrl);
                if (!hasSource)
                {
                    AddIssue(result, "sibling_source_unavailable", "provisioning",
                        sibling.BuildParticipant ? ReadinessSeverityEnum.Error : ReadinessSeverityEnum.Warning,
                        "Armada cannot resolve the source for sibling repository " + sibling.RelativePath + ".", sibling.VesselRef);
                }

                if (sibling.ExtractionArtifactPaths == null || sibling.ExtractionArtifactPaths.Count == 0) continue;
                if (siblingVessel == null || String.IsNullOrWhiteSpace(siblingVessel.WorkingDirectory))
                {
                    AddIssue(result, "sibling_artifact_source_unavailable", "provisioning", ReadinessSeverityEnum.Error,
                        "The sibling extraction artifacts require a registered vessel with a working directory.", sibling.RelativePath);
                    continue;
                }

                foreach (string artifactPath in sibling.ExtractionArtifactPaths.Where(item => !String.IsNullOrWhiteSpace(item)))
                {
                    string sourcePath = Path.Combine(siblingVessel.WorkingDirectory, artifactPath);
                    if (!Directory.Exists(sourcePath))
                    {
                        AddIssue(result, "sibling_artifact_missing", "provisioning", ReadinessSeverityEnum.Error,
                            "A required sibling extraction artifact path does not exist.", sourcePath);
                    }
                }
            }
        }

        private async Task<Vessel?> ResolveVesselReferenceAsync(
            AuthContext auth,
            string? vesselRef,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(vesselRef)) return null;
            return await ReadVesselAsync(auth, vesselRef, token).ConfigureAwait(false)
                ?? await ReadVesselByNameAsync(auth, vesselRef, token).ConfigureAwait(false);
        }

        private static string NormalizeRelativePath(string? value)
        {
            return (value ?? String.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        private async Task<Pipeline?> ResolvePipelineReadOnlyAsync(
            Vessel vessel,
            string? explicitPipeline,
            bool readOnly,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            // The preview applies the same ownership rule as dispatch: a pipeline is used on behalf of
            // the vessel's owner, and a record that owner may not use is reported as refused, not missing.
            if (!String.IsNullOrWhiteSpace(explicitPipeline))
            {
                Pipeline? requested = await _Database.Pipelines.ReadAsync(explicitPipeline, token).ConfigureAwait(false);
                bool refused = false;
                if (requested != null)
                {
                    if (!OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, requested))
                    {
                        requested = null;
                        refused = true;
                    }
                }
                else
                {
                    OwnedRecordLookup<Pipeline> lookup = await OwnedRecordScope.ReadUsableByNameAsync(
                        vessel.TenantId,
                        vessel.UserId,
                        explicitPipeline,
                        () => _Database.Pipelines.EnumerateAsync(token),
                        pipeline => pipeline.Name).ConfigureAwait(false);
                    requested = lookup.Record;
                    refused = lookup.WasRefused;
                }

                if (refused)
                    AddIssue(result, "pipeline_not_usable", "pipeline", ReadinessSeverityEnum.Error,
                        "The requested pipeline belongs to another owner and cannot be used for this vessel.", explicitPipeline);
                else if (requested == null)
                    AddIssue(result, "pipeline_not_found", "pipeline", ReadinessSeverityEnum.Error,
                        "The requested pipeline does not exist.", explicitPipeline);
                else
                    ValidatePipeline(requested, result);
                return requested;
            }

            if (readOnly) return null;

            string? inheritedPipelineId = NormalizeEmpty(vessel.DefaultPipelineId);
            if (inheritedPipelineId == null && !String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                Fleet? fleet = await _Database.Fleets.ReadAsync(vessel.FleetId, token).ConfigureAwait(false);
                inheritedPipelineId = NormalizeEmpty(fleet?.DefaultPipelineId);
            }
            if (inheritedPipelineId == null) return null;

            Pipeline? inherited = await _Database.Pipelines.ReadAsync(inheritedPipelineId, token).ConfigureAwait(false);
            if (inherited == null)
            {
                AddIssue(result, "default_pipeline_not_found", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective default pipeline does not exist.", inheritedPipelineId);
                return null;
            }
            if (!OwnershipPolicy.CanUseFor(vessel.TenantId, vessel.UserId, inherited))
            {
                AddIssue(result, "default_pipeline_not_usable", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective default pipeline belongs to another owner and is not inherited by this vessel.", inheritedPipelineId);
                return null;
            }
            ValidatePipeline(inherited, result);
            return inherited;
        }

        private static void ValidatePipeline(Pipeline pipeline, ObjectiveDispatchPreview result)
        {
            if (!pipeline.Active)
                AddIssue(result, "pipeline_inactive", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective pipeline is not active.", pipeline.Id);
            if (pipeline.Stages == null || pipeline.Stages.Count == 0)
                AddIssue(result, "pipeline_has_no_stages", "pipeline", ReadinessSeverityEnum.Error,
                    "The effective pipeline has no stages.", pipeline.Id);
        }

        /// <summary>
        /// Consult the D19 stage-necessity adapter over the resolved pipeline stages and add one
        /// <c>stage_optional</c> Warning issue per non-Judge stage the model proposes the objective does
        /// not need. The deterministic pipeline is unchanged: the warning is advisory and the operator
        /// confirms a skip through <c>skipStages</c> before the voyage is materialised. Never throws into
        /// the preview.
        /// </summary>
        private async Task RefineStageNecessityAsync(
            Objective objective,
            Vessel vessel,
            Pipeline? pipeline,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (StageNecessityAdapter == null || objective == null) return;

            List<StageNecessityStageResult> stages = (pipeline?.Stages ?? new List<PipelineStage>())
                .Where(stage => stage != null)
                .OrderBy(stage => stage.Order)
                .Select(stage => TypedStageNecessityAdapter.Stage(stage.Order, stage.PersonaName))
                .ToList();
            if (stages.Count == 0)
                stages.Add(TypedStageNecessityAdapter.Stage(1, "Worker"));

            Dictionary<string, string> descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PipelineStage stage in (pipeline?.Stages ?? new List<PipelineStage>()).Where(item => item != null))
            {
                if (!String.IsNullOrWhiteSpace(stage.Description) && !descriptions.ContainsKey(stage.PersonaName))
                    descriptions[stage.PersonaName] = stage.Description!;
            }

            StageNecessityDecisionInput input = new StageNecessityDecisionInput
            {
                Title = objective.Title ?? String.Empty,
                Description = objective.Description ?? String.Empty,
                AcceptanceCriteria = (objective.AcceptanceCriteria ?? new List<string>())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).ToList(),
                Kind = objective.Kind.ToString(),
                Stages = stages,
                StageDescriptions = descriptions
            };

            // The adapter is contracted never to throw into the caller: Off returns the rule with no
            // call, and every fault path returns the rule verdict. So a raised exception cannot break the
            // deterministic preview, exactly as the D5 preflight adapter is called above.
            StageNecessityVerdict rule = StageNecessityVerdict.Rule(stages);
            StageNecessityVerdict verdict = await StageNecessityAdapter.DecideAsync(input, rule, token).ConfigureAwait(false);

            foreach (StageNecessityStageResult stage in verdict.OptionalStages)
            {
                string suffix = stage.ModelAutoSkip ? " (auto-skip eligible)" : String.Empty;
                AddIssue(result, "stage_optional", "pipeline", ReadinessSeverityEnum.Warning,
                    "The " + stage.PersonaName + " stage may not be needed for this objective ("
                    + stage.SkipReason + "); confirm a skip through skipStages if so." + suffix,
                    stage.PersonaName);
            }
        }

        private void EvaluateCaptainCoverage(
            Pipeline? pipeline,
            List<Captain> captains,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            ObjectiveDispatchPreview result)
        {
            List<PipelineStage> stages = pipeline?.Stages?.ToList()
                ?? new List<PipelineStage> { new PipelineStage(1, "Worker") };
            IReadOnlyCollection<string> specialistPersonas = _Settings.ModelTier.SpecialistPersonas;
            foreach (PipelineStage stage in stages
                .Where(item => item != null)
                .OrderBy(item => item.Order))
            {
                List<string?> resolvedPreferences = ResolveStagePreferredModels(
                    stage, missionDescriptions, specialistPersonas);
                foreach (string? preferredModel in resolvedPreferences.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    CaptainAssignmentOverride? assignment = MissionService.SelectCaptainOverride(captainAssignments, stage.PersonaName);
                    CaptainTierEnum? fallbackTier = assignment?.FallbackTier;
                    List<Captain> configured = captains
                        .Where(IsConfiguredUsableCaptain)
                        .Where(captain => MissionService.CaptainSatisfiesPreferredRouting(
                            captain, stage.PersonaName, preferredModel, _Settings.ModelTier))
                        .Where(captain => fallbackTier == null || CaptainTierSelector.EffectiveTier(captain) >= fallbackTier.Value)
                        .ToList();

                    if (assignment != null && !String.IsNullOrWhiteSpace(assignment.CaptainId))
                    {
                        Captain? selected = captains.FirstOrDefault(captain => String.Equals(
                            captain.Id, assignment.CaptainId, StringComparison.Ordinal));
                        if (selected == null || !IsConfiguredUsableCaptain(selected)
                            || !MissionService.CaptainSatisfiesPreferredRouting(
                                selected, stage.PersonaName, preferredModel, _Settings.ModelTier))
                        {
                            AddIssue(result, "assigned_captain_ineligible", "captain", ReadinessSeverityEnum.Error,
                                "The assigned captain cannot run the " + stage.PersonaName + " role.", assignment.CaptainId);
                        }
                        else if (!configured.Any(captain => String.Equals(captain.Id, selected.Id, StringComparison.Ordinal)))
                        {
                            configured.Add(selected);
                        }
                    }

                    ObjectiveDispatchRole role = new ObjectiveDispatchRole
                    {
                        Persona = stage.PersonaName,
                        PreferredModel = preferredModel,
                        EligibleConfiguredCaptainIds = configured.Select(captain => captain.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                        IdleEligibleCount = configured.Count(captain => captain.State == CaptainStateEnum.Idle)
                    };
                    result.RequiredRoles.Add(role);

                    if (configured.Count == 0)
                    {
                        AddIssue(result, "required_role_has_no_captain", "captain", ReadinessSeverityEnum.Error,
                            "No configured captain can run the required " + stage.PersonaName + " role.", preferredModel);
                    }
                    else if (role.IdleEligibleCount == 0)
                    {
                        AddIssue(result, "required_role_has_no_idle_captain", "captain", ReadinessSeverityEnum.Warning,
                            "The required " + stage.PersonaName + " role has configured coverage but no idle capacity.", preferredModel);
                    }
                }
            }
        }

        private static List<string?> ResolveStagePreferredModels(
            PipelineStage stage,
            IReadOnlyList<MissionDescription>? missionDescriptions,
            IReadOnlyCollection<string> specialistPersonas)
        {
            List<string?> resolved = new List<string?>();
            if (missionDescriptions == null || missionDescriptions.Count == 0)
            {
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel, null, stage.PersonaName, specialistPersonas));
                return resolved;
            }

            foreach (MissionDescription missionDescription in missionDescriptions)
            {
                if (missionDescription == null) continue;
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel,
                    missionDescription.PreferredModel,
                    stage.PersonaName,
                    specialistPersonas));
            }

            if (resolved.Count == 0)
            {
                resolved.Add(PreferredModelTierSelector.ResolveEffectivePreferredModel(
                    stage.PreferredModel, null, stage.PersonaName, specialistPersonas));
            }

            return resolved;
        }

        private async Task EvaluateChecksAsync(
            AuthContext auth,
            Vessel vessel,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            VoyageCheckArmingSettings arming = _Settings.VoyageCheckArming;
            if (!arming.Enabled)
            {
                AddIssue(result, "check_arming_disabled", "verification", ReadinessSeverityEnum.Error,
                    "Voyage Check arming is disabled, so dispatch cannot produce the required verification records.", null);
                return;
            }

            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);
            if (profile == null)
            {
                AddIssue(result, "workflow_profile_missing", "verification", ReadinessSeverityEnum.Error,
                    "No active workflow profile resolves for the target vessel.", vessel.Id);
                AddRequiredCheck(result, CheckRunTypeEnum.Build, null, false, arming.ArmBuild);
                AddRequiredCheck(result, CheckRunTypeEnum.UnitTest, null, false, arming.ArmUnitTest);
                return;
            }

            await EvaluateRequiredCheckAsync(auth, vessel, profile, CheckRunTypeEnum.Build, arming.ArmBuild, result, token).ConfigureAwait(false);
            await EvaluateRequiredCheckAsync(auth, vessel, profile, CheckRunTypeEnum.UnitTest, arming.ArmUnitTest, result, token).ConfigureAwait(false);

            // The preview asks the same plan dispatch uses, so it cannot promise a Slop check the
            // arming service would not create.
            bool isDotNet = DotNetVesselDetector.IsDotNetVessel(vessel, profile, out string _);
            if (VoyageCheckArmingPlan.Resolve(arming, profile, null, false, isDotNet).Contains(CheckRunTypeEnum.Slop))
                AddRequiredCheck(result, CheckRunTypeEnum.Slop, SlopCheckRunner.CommandLabel, true, true);
        }

        private async Task EvaluateRequiredCheckAsync(
            AuthContext auth,
            Vessel vessel,
            WorkflowProfile profile,
            CheckRunTypeEnum type,
            bool required,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (!required) return;
            VesselReadinessResult readiness = await _VesselReadiness.EvaluateAsync(
                auth,
                vessel,
                profile.Id,
                type,
                includeWorkflowRequirements: true,
                token: token).ConfigureAwait(false);
            bool inputsReady = !readiness.Issues.Any(issue => String.Equals(
                issue.Code, "required_input_missing", StringComparison.OrdinalIgnoreCase));
            foreach (VesselReadinessIssue issue in readiness.Issues.Where(issue =>
                String.Equals(issue.Code, "required_input_missing", StringComparison.OrdinalIgnoreCase)
                || String.Equals(issue.Code, "workflow_profile_invalid", StringComparison.OrdinalIgnoreCase)
                || String.Equals(issue.Code, "command_dependency_missing", StringComparison.OrdinalIgnoreCase)))
            {
                AddIssue(result, issue.Code, "verification", ReadinessSeverityEnum.Error, issue.Message, issue.RelatedValue);
            }
            AddRequiredCheck(result, type, _WorkflowProfiles.ResolveCommand(profile, type), inputsReady, true);
        }

        private static void AddRequiredCheck(
            ObjectiveDispatchPreview result,
            CheckRunTypeEnum type,
            string? command,
            bool inputsReady,
            bool required)
        {
            if (!required) return;
            result.RequiredChecks.Add(new ObjectiveDispatchCheck
            {
                Type = type,
                Command = NormalizeEmpty(command),
                RequiredInputsReady = inputsReady
            });
            if (String.IsNullOrWhiteSpace(command))
            {
                AddIssue(result, "check_command_missing", "verification", ReadinessSeverityEnum.Error,
                    "No command is configured for required Check " + type + ".", type.ToString());
            }
        }

        private async Task<Vessel?> ReadVesselAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadAsync(id, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            if (auth.IsTenantAdmin) return await _Database.Vessels.ReadAsync(auth.TenantId, id, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return await _Database.Vessels.ReadAsync(auth.TenantId, auth.UserId, id, token).ConfigureAwait(false);
        }

        private async Task<Vessel?> ReadVesselByNameAsync(AuthContext auth, string name, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Vessels.ReadByNameAsync(name, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            Vessel? vessel = await _Database.Vessels.ReadByNameAsync(auth.TenantId, name, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin) return vessel;
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return vessel != null && String.Equals(vessel.UserId, auth.UserId, StringComparison.Ordinal)
                ? vessel
                : null;
        }

        private async Task<List<Captain>> ReadCaptainsAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return new List<Captain>();
            if (auth.IsTenantAdmin) return await _Database.Captains.EnumerateAsync(auth.TenantId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return new List<Captain>();
            return await _Database.Captains.EnumerateAsync(auth.TenantId, auth.UserId, token).ConfigureAwait(false);
        }

        private async Task<List<Objective>> ReadObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return new List<Objective>();
            if (auth.IsTenantAdmin) return await _Database.Objectives.EnumerateAsync(auth.TenantId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return new List<Objective>();
            return await _Database.Objectives.EnumerateAsync(auth.TenantId, auth.UserId, token).ConfigureAwait(false);
        }

        private static bool IsConfiguredUsableCaptain(Captain captain)
        {
            return captain.State != CaptainStateEnum.Benched
                && captain.State != CaptainStateEnum.Quarantined
                && captain.State != CaptainStateEnum.Stalled
                && captain.State != CaptainStateEnum.Stopping;
        }

        private string RepositoryPath(Vessel vessel)
        {
            return vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
        }

        private static bool RevisionMatches(string left, string right)
        {
            return left.Length == 40
                && right.Length == 40
                && left.All(Uri.IsHexDigit)
                && right.All(Uri.IsHexDigit)
                && String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadOnlyMissionMode(string mode)
        {
            MissionModeEnum parsed = MissionModes.Parse(mode);
            return parsed == MissionModeEnum.Audit || parsed == MissionModeEnum.Research;
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// Checks declared execution requirements against the environment captains launch in. A dependency
        /// that is readable on the Admiral host does not prove a captain can load or run it, so each
        /// requirement is checked on its own and every unavailable one is a blocking finding. Read-only:
        /// executables are resolved by lookup and never run.
        /// </summary>
        private static void EvaluateExecutionRequirements(
            Objective objective,
            ICaptainExecutionEnvironmentProbe environment,
            ObjectiveDispatchPreview result)
        {
            ObjectiveExecutionRequirements? requirements = objective.Preparation?.ExecutionRequirements;
            if (requirements == null || !requirements.HasAny) return;

            const string area = "execution";

            if (!String.IsNullOrWhiteSpace(requirements.OperatingSystem)
                && !String.Equals(requirements.OperatingSystem, environment.OperatingSystem, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(result, "execution_operating_system_unavailable", area, ReadinessSeverityEnum.Error,
                    "Captains execute on " + environment.OperatingSystem + ", not the required " + requirements.OperatingSystem + ".",
                    requirements.OperatingSystem);
            }

            if (!String.IsNullOrWhiteSpace(requirements.Architecture)
                && !String.Equals(requirements.Architecture, environment.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(result, "execution_architecture_unavailable", area, ReadinessSeverityEnum.Error,
                    "Captains execute on " + environment.Architecture + ", not the required " + requirements.Architecture + " architecture.",
                    requirements.Architecture);
            }

            foreach (string executable in requirements.Executables ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(executable)) continue;
                if (environment.ResolveExecutable(executable) != null) continue;
                AddIssue(result, "execution_executable_unavailable", area, ReadinessSeverityEnum.Error,
                    "Required executable " + executable + " is not available to captains. A readable file is not a runnable one without it.",
                    executable);
            }

            foreach (string dependency in requirements.DependencyPaths ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(dependency)) continue;
                if (environment.PathExists(dependency)) continue;
                AddIssue(result, "execution_dependency_unavailable", area, ReadinessSeverityEnum.Error,
                    "Required dependency path is not present in the captain execution environment.", dependency);
            }

            if (!String.IsNullOrWhiteSpace(requirements.IsolationBoundary))
            {
                bool wantsContainer = String.Equals(requirements.IsolationBoundary, "Container", StringComparison.OrdinalIgnoreCase);
                if (wantsContainer != environment.IsContainer)
                {
                    AddIssue(result, "execution_isolation_unavailable", area, ReadinessSeverityEnum.Error,
                        "Captains execute " + (environment.IsContainer ? "inside a container" : "directly on the host") +
                        ", not behind the required " + requirements.IsolationBoundary + " boundary.",
                        requirements.IsolationBoundary);
                }
            }

            if (!String.IsNullOrWhiteSpace(requirements.LicensedContext)
                && !(environment.LicensedContexts ?? new List<string>()).Any(name => String.Equals(name, requirements.LicensedContext, StringComparison.OrdinalIgnoreCase)))
            {
                AddIssue(result, "execution_licensed_context_unavailable", area, ReadinessSeverityEnum.Error,
                    "Licensed context " + requirements.LicensedContext + " is not available to captains.",
                    requirements.LicensedContext);
            }
        }

        private static void AddIssue(
            ObjectiveDispatchPreview result,
            string code,
            string area,
            ReadinessSeverityEnum severity,
            string message,
            string? relatedValue)
        {
            result.Issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = code,
                Area = area,
                Severity = severity,
                Message = message,
                RelatedValue = relatedValue
            });
        }

        private static void FinalizeResult(ObjectiveDispatchPreview result)
        {
            result.ErrorCount = result.Issues.Count(issue => issue.Severity == ReadinessSeverityEnum.Error);
            result.WarningCount = result.Issues.Count(issue => issue.Severity == ReadinessSeverityEnum.Warning);
            result.IsReady = result.ErrorCount == 0;
        }

        #endregion
    }
}
