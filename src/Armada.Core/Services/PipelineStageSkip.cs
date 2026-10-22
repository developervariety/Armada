namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule for dropping operator-confirmed stages from a pipeline before a voyage is
    /// materialised. Every dispatch path (REST, MCP, WebSocket, alias dispatch and the autonomous
    /// scheduler) applies it to the resolved pipeline and then materialises the returned pipeline
    /// unchanged. The skipped stages are removed BEFORE stages are grouped by order, so the
    /// materialiser's normal chaining links each remaining stage to the last mission of the previous
    /// remaining order group: <c>DependsOnMissionId</c> spans the gap and no stage is ever created
    /// with a null dependency because its predecessor was skipped. Each skip entry names exactly one
    /// stage: by persona when one stage carries it, by stage order (<c>5</c> or <c>#5</c>), or by
    /// persona and occurrence in stage order (<c>Worker#2</c>).
    /// </summary>
    public static class PipelineStageSkip
    {
        #region Public-Members

        /// <summary>Event type recorded once per skipped stage.</summary>
        public const string StageSkippedEventType = "voyage.stage_skipped";

        /// <summary>Refusal code: the request tried to skip the Judge.</summary>
        public const string JudgeRefusedCode = "stage_skip_judge_refused";

        /// <summary>Refusal code: a skip entry is blank, malformed, or names no stage of the effective pipeline.</summary>
        public const string UnknownPersonaCode = "stage_skip_unknown_persona";

        /// <summary>Refusal code: the skip would leave no stage other than the Judge.</summary>
        public const string NoWorkRemainsCode = "stage_skip_leaves_no_work";

        /// <summary>Refusal code: a bare persona name that more than one stage carries, or an order more than one parallel stage shares.</summary>
        public const string AmbiguousPersonaCode = "stage_skip_ambiguous_persona";

        /// <summary>
        /// Preview Warning code: an entry of the objective's stored default skip names no stage of the
        /// chosen pipeline, so the dispatch drops it instead of refusing.
        /// </summary>
        public const string DefaultDroppedCode = "stage_skip_default_dropped";

        /// <summary>Event type recorded on a voyage once per stored default skip entry the dispatch dropped.</summary>
        public const string DefaultDroppedEventType = "voyage.stage_skip_default_dropped";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Refuse the parts of a skip request that need no pipeline to judge: a blank or malformed entry,
        /// or an entry whose persona is the Judge.
        /// </summary>
        /// <param name="request">Skip request, or null.</param>
        /// <exception cref="StageSkipRefusedException">When an entry is blank or malformed, or names the Judge.</exception>
        public static void ValidateNamesOrThrow(StageSkipRequest? request)
        {
            if (!StageSkipRequest.HasStages(request)) return;

            foreach (string? name in request!.Stages)
            {
                StageSelector selector = StageSelector.Parse(name);
                if (selector.Malformed)
                {
                    throw new StageSkipRefusedException(UnknownPersonaCode, name,
                        String.IsNullOrWhiteSpace(name)
                            ? "skipStages contains a blank persona name."
                            : "skipStages names \"" + name + "\", which is not a stage name. " + NamingHelp);
                }

                if (selector.Persona != null && PersonaCatalog.Matches(selector.Persona, PersonaCatalog.Judge))
                {
                    throw new StageSkipRefusedException(JudgeRefusedCode, name,
                        "The Judge stage cannot be skipped: every voyage keeps its verdict.");
                }
            }
        }

        /// <summary>
        /// Build the skip request for an operator dispatch, naming the calling principal as the confirmer.
        /// </summary>
        /// <param name="stages">Persona names to skip, or null.</param>
        /// <param name="reason">Operator-supplied reason, or null.</param>
        /// <param name="caller">Calling principal, or null.</param>
        /// <returns>The request, or null when no stage is named.</returns>
        public static StageSkipRequest? FromOperator(List<string>? stages, string? reason, AuthContext? caller)
        {
            if (stages == null || stages.Count == 0) return null;
            return new StageSkipRequest
            {
                Stages = stages.ToList(),
                Reason = String.IsNullOrWhiteSpace(reason) ? "operator-confirmed at dispatch" : reason.Trim(),
                ConfirmedBy = DescribeConfirmer(caller),
                ConfirmedUtc = DateTime.UtcNow,
                OperatorConfirmationId = Guid.NewGuid().ToString("N")
            };
        }

        /// <summary>
        /// The skip a dispatch inherits from an objective's stored <c>preparation.stageSkip</c>: a copy
        /// marked <see cref="StageSkipRequest.IsObjectiveDefault"/>, or null when nothing is stored or the
        /// stored skip has no server-issued operator confirmation. A dispatch request that names its own
        /// <c>skipStages</c>, an empty list included, uses that list instead.
        /// </summary>
        /// <param name="stored">The objective's stored skip, or null.</param>
        /// <returns>The default skip to apply, or null.</returns>
        public static StageSkipRequest? AsObjectiveDefault(StageSkipRequest? stored)
        {
            if (!StageSkipRequest.HasTrustedConfirmation(stored)) return null;
            return new StageSkipRequest
            {
                Stages = stored!.Stages.ToList(),
                Reason = stored.Reason,
                ConfirmedBy = stored.ConfirmedBy,
                ConfirmedUtc = stored.ConfirmedUtc,
                OperatorConfirmationId = stored.OperatorConfirmationId,
                IsObjectiveDefault = true
            };
        }

        /// <summary>
        /// A readable label for who confirmed a skip: the principal display, else the user id, else
        /// <c>operator</c>.
        /// </summary>
        /// <param name="caller">Calling principal, or null.</param>
        /// <returns>Confirmer label.</returns>
        public static string DescribeConfirmer(AuthContext? caller)
        {
            if (!String.IsNullOrWhiteSpace(caller?.PrincipalDisplay)) return caller!.PrincipalDisplay!.Trim();
            if (!String.IsNullOrWhiteSpace(caller?.UserId)) return caller!.UserId!.Trim();
            return "operator";
        }

        /// <summary>
        /// Apply a skip request to the resolved pipeline. A null pipeline is the single-stage Worker
        /// dispatch. Returns the pipeline to materialise: the same instance when nothing is skipped,
        /// otherwise a copy without the named stages.
        /// </summary>
        /// <param name="pipeline">Resolved pipeline, or null for a single-stage Worker dispatch.</param>
        /// <param name="request">Skip request, or null.</param>
        /// <returns>The pipeline to materialise and the stages that were dropped.</returns>
        /// <remarks>
        /// An entry of an objective's stored default skip (<see cref="StageSkipRequest.IsObjectiveDefault"/>)
        /// that names no stage of the pipeline is dropped and listed in
        /// <see cref="PipelineStageSkipResult.DroppedDefaults"/>, because the default was written for
        /// whichever pipeline the objective usually runs. Every other refusal holds for a default too.
        /// </remarks>
        /// <exception cref="StageSkipRefusedException">
        /// When an entry is blank or malformed, names the Judge, names no stage of the pipeline (an entry
        /// the request named), names more than one stage of the pipeline, or the skip would leave no stage
        /// other than the Judge.
        /// </exception>
        public static PipelineStageSkipResult Apply(Pipeline? pipeline, StageSkipRequest? request)
        {
            if (!StageSkipRequest.HasStages(request))
                return new PipelineStageSkipResult(pipeline, new List<PipelineStage>());
            if (!request!.IsObjectiveDefault)
                return ApplyRequested(pipeline, request);

            try
            {
                return ApplyRequested(pipeline, request);
            }
            catch (StageSkipRefusedException refused)
            {
                throw new StageSkipRefusedException(refused.Code, refused.Persona,
                    "The objective's stored stage skip (preparation.stageSkip) cannot be applied: " + refused.Message
                    + " Correct the stored skip, or name this dispatch's own skipStages (an empty list skips nothing).");
            }
        }

        private static PipelineStageSkipResult ApplyRequested(Pipeline? pipeline, StageSkipRequest request)
        {
            ValidateNamesOrThrow(request);

            List<PipelineStage> stages = (pipeline?.Stages ?? new List<PipelineStage> { new PipelineStage(1, PersonaCatalog.Worker) })
                .Where(stage => stage != null)
                .ToList();
            string pipelineName = pipeline?.Name ?? "single-stage Worker dispatch";

            // Occurrence numbers count the stages of one persona in stage order, so "Worker#2" is the
            // second Worker however the stage list happens to be stored.
            List<PipelineStage> ordered = stages
                .Select((stage, index) => new { stage, index })
                .OrderBy(item => item.stage.Order)
                .ThenBy(item => item.index)
                .Select(item => item.stage)
                .ToList();

            HashSet<PipelineStage> selected = new HashSet<PipelineStage>();
            List<DroppedStageSkip> droppedDefaults = new List<DroppedStageSkip>();
            foreach (string name in request.Stages)
            {
                PipelineStage stage;
                try
                {
                    stage = ResolveOrThrow(ordered, name, pipelineName);
                }
                catch (StageSkipRefusedException unknown) when (request.IsObjectiveDefault && unknown.Code == UnknownPersonaCode)
                {
                    droppedDefaults.Add(new DroppedStageSkip(name, unknown.Message));
                    continue;
                }
                if (PersonaCatalog.Matches(stage.PersonaName, PersonaCatalog.Judge))
                {
                    throw new StageSkipRefusedException(JudgeRefusedCode, name,
                        "skipStages names \"" + name + "\", which is the Judge stage (order " + stage.Order
                        + "). The Judge stage cannot be skipped: every voyage keeps its verdict.");
                }
                selected.Add(stage);
            }

            if (selected.Count == 0)
                return new PipelineStageSkipResult(pipeline, new List<PipelineStage>(), droppedDefaults);

            List<PipelineStage> skipped = stages.Where(stage => selected.Contains(stage)).ToList();
            List<PipelineStage> kept = stages.Where(stage => !skipped.Contains(stage)).ToList();

            if (!kept.Any(stage => !PersonaCatalog.Matches(stage.PersonaName, PersonaCatalog.Judge)))
            {
                throw new StageSkipRefusedException(NoWorkRemainsCode, null,
                    "skipStages would leave no stage other than the Judge in " + pipelineName + ".");
            }

            Pipeline effective = new Pipeline(pipeline?.Name ?? "WorkerOnly")
            {
                Id = pipeline?.Id ?? new Pipeline().Id,
                TenantId = pipeline?.TenantId,
                UserId = pipeline?.UserId,
                OwnershipScope = pipeline?.OwnershipScope ?? default,
                Description = pipeline?.Description,
                IsBuiltIn = pipeline?.IsBuiltIn ?? false,
                Active = pipeline?.Active ?? true,
                CreatedUtc = pipeline?.CreatedUtc ?? DateTime.UtcNow,
                LastUpdateUtc = pipeline?.LastUpdateUtc ?? DateTime.UtcNow,
                Stages = kept
            };
            return new PipelineStageSkipResult(effective, skipped, droppedDefaults);
        }

        /// <summary>
        /// Record one <c>voyage.stage_skipped</c> event per skipped stage, naming the persona, the
        /// reason and who confirmed the skip, and one <c>voyage.stage_skip_default_dropped</c> event per
        /// stored default entry the pipeline does not carry. A failure to record is logged, never thrown
        /// into the dispatch that already created the voyage.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module, or null.</param>
        /// <param name="voyage">The created voyage.</param>
        /// <param name="result">The skip result from <see cref="Apply"/>.</param>
        /// <param name="request">The skip request.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task EmitSkippedEventsAsync(
            DatabaseDriver database,
            LoggingModule? logging,
            Voyage voyage,
            PipelineStageSkipResult result,
            StageSkipRequest? request,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (voyage == null || result == null) return;
            if (result.SkippedStages.Count == 0 && result.DroppedDefaults.Count == 0) return;

            string reason = String.IsNullOrWhiteSpace(request?.Reason) ? "operator-confirmed skip" : request!.Reason!.Trim();
            string confirmer = String.IsNullOrWhiteSpace(request?.ConfirmedBy) ? "unknown" : request!.ConfirmedBy!.Trim();

            foreach (DroppedStageSkip dropped in result.DroppedDefaults)
            {
                try
                {
                    ArmadaEvent evt = new ArmadaEvent(DefaultDroppedEventType,
                        "Stored default stage skip \"" + dropped.Entry + "\" dropped on voyage " + voyage.Id
                        + " because it names no stage of the pipeline: " + dropped.Reason);
                    evt.EntityType = "voyage";
                    evt.EntityId = voyage.Id;
                    evt.VoyageId = voyage.Id;
                    evt.TenantId = voyage.TenantId;
                    evt.UserId = voyage.UserId;
                    evt.Payload = JsonSerializer.Serialize(new DefaultDroppedPayload
                    {
                        Entry = dropped.Entry,
                        Reason = dropped.Reason,
                        Confirmer = confirmer
                    });
                    await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logging?.Warn("[PipelineStageSkip] could not record " + DefaultDroppedEventType + " for voyage "
                        + voyage.Id + " entry " + dropped.Entry + ": " + ex.Message);
                }
            }

            foreach (PipelineStage stage in result.SkippedStages)
            {
                try
                {
                    ArmadaEvent evt = new ArmadaEvent(StageSkippedEventType,
                        "Stage " + stage.PersonaName + " (order " + stage.Order + ") skipped on voyage " + voyage.Id
                        + " (reason: " + reason + "; confirmed by " + confirmer + ").");
                    evt.EntityType = "voyage";
                    evt.EntityId = voyage.Id;
                    evt.VoyageId = voyage.Id;
                    evt.TenantId = voyage.TenantId;
                    evt.UserId = voyage.UserId;
                    evt.Payload = JsonSerializer.Serialize(new StageSkippedPayload
                    {
                        Persona = stage.PersonaName,
                        StageOrder = stage.Order,
                        Reason = reason,
                        Confirmer = confirmer
                    });
                    await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logging?.Warn("[PipelineStageSkip] could not record " + StageSkippedEventType + " for voyage "
                        + voyage.Id + " stage " + stage.PersonaName + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// How a skip entry names one stage, quoted in every refusal that cannot resolve an entry.
        /// </summary>
        public const string NamingHelp =
            "Name a stage by its persona when only one stage carries it (\"Linter\"), by its stage order (\"5\" or \"#5\"), "
            + "or by persona and occurrence in stage order (\"Worker#2\" is the second Worker).";

        /// <summary>
        /// Resolve one skip entry to the single stage it names. An entry is a persona name, a stage
        /// order (<c>5</c> or <c>#5</c>), or a persona with its 1-based occurrence in stage order
        /// (<c>Worker#2</c>). A persona name that more than one stage carries, or an order that more
        /// than one parallel stage shares, names no single stage and is refused as ambiguous.
        /// </summary>
        /// <param name="ordered">Pipeline stages in stage order.</param>
        /// <param name="name">Requested skip entry.</param>
        /// <param name="pipelineName">Pipeline name for refusal messages.</param>
        /// <returns>The named stage.</returns>
        /// <exception cref="StageSkipRefusedException">When the entry names no stage or more than one.</exception>
        private static PipelineStage ResolveOrThrow(List<PipelineStage> ordered, string name, string pipelineName)
        {
            StageSelector selector = StageSelector.Parse(name);
            string listing = pipelineName + "; stages: " + DescribeStages(ordered);

            if (selector.Order.HasValue)
            {
                List<PipelineStage> atOrder = ordered.Where(stage => stage.Order == selector.Order.Value).ToList();
                if (atOrder.Count == 0)
                {
                    throw new StageSkipRefusedException(UnknownPersonaCode, name,
                        "skipStages names \"" + name + "\", but no stage of the effective pipeline has order "
                        + selector.Order.Value + " (" + listing + ").");
                }
                if (atOrder.Count > 1)
                {
                    throw new StageSkipRefusedException(AmbiguousPersonaCode, name,
                        "skipStages names \"" + name + "\", but " + atOrder.Count + " parallel stages share order "
                        + selector.Order.Value + " (" + listing + "). " + NamingHelp);
                }
                return atOrder[0];
            }

            List<PipelineStage> named = ordered.Where(stage => NamesStage(stage.PersonaName, selector.Persona)).ToList();
            if (named.Count == 0)
            {
                throw new StageSkipRefusedException(UnknownPersonaCode, name,
                    "skipStages names \"" + name + "\", which is not a stage of the effective pipeline ("
                    + listing + ").");
            }

            if (selector.Occurrence.HasValue)
            {
                if (selector.Occurrence.Value > named.Count)
                {
                    throw new StageSkipRefusedException(UnknownPersonaCode, name,
                        "skipStages names \"" + name + "\", but the effective pipeline has only " + named.Count + " "
                        + named[0].PersonaName + " stage(s) (" + listing + ").");
                }
                return named[selector.Occurrence.Value - 1];
            }

            if (named.Count > 1)
            {
                throw new StageSkipRefusedException(AmbiguousPersonaCode, name,
                    "skipStages names \"" + name + "\", which is the persona of " + named.Count + " stages of the effective pipeline ("
                    + listing + "). A persona name cannot say which of them to skip; name one as "
                    + String.Join(" or ", named.Select((stage, index) => "\"" + stage.Order + "\" / \"" + stage.PersonaName.Replace(" ", String.Empty) + "#" + (index + 1) + "\""))
                    + ". " + NamingHelp);
            }

            return named[0];
        }

        private static string DescribeStages(List<PipelineStage> ordered)
        {
            return String.Join(", ", ordered.Select(stage => stage.Order + " " + stage.PersonaName));
        }

        /// <summary>
        /// Whether a requested skip name names a pipeline stage. A stage's persona name can hold spaces
        /// ("Product Manager") while callers write the persona identifier form ("ProductManager"), so
        /// the comparison ignores spaces as well as case.
        /// </summary>
        /// <param name="stagePersona">Persona name of the pipeline stage.</param>
        /// <param name="requested">Requested skip name.</param>
        /// <returns>True when the name names the stage.</returns>
        public static bool NamesStage(string? stagePersona, string? requested)
        {
            if (PersonaCatalog.Matches(stagePersona, requested)) return true;
            string left = PersonaCatalog.NormalizeName(stagePersona).Replace(" ", String.Empty);
            string right = PersonaCatalog.NormalizeName(requested).Replace(" ", String.Empty);
            return left.Length > 0 && String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Private-Members

        // One parsed skip entry: exactly one of Order or Persona is set; Occurrence qualifies Persona.
        private sealed class StageSelector
        {
            public string? Persona { get; private set; }
            public int? Order { get; private set; }
            public int? Occurrence { get; private set; }
            public bool Malformed { get; private set; }

            public static StageSelector Parse(string? raw)
            {
                StageSelector selector = new StageSelector();
                string text = (raw ?? String.Empty).Trim();
                if (text.Length == 0)
                {
                    selector.Malformed = true;
                    return selector;
                }

                string orderText = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1).Trim() : text;
                if (orderText.Length > 0 && orderText.All(Char.IsDigit))
                {
                    if (Int32.TryParse(orderText, out int order) && order > 0) selector.Order = order;
                    else selector.Malformed = true;
                    return selector;
                }
                if (orderText.Length == 0)
                {
                    selector.Malformed = true;
                    return selector;
                }

                int hash = text.LastIndexOf('#');
                if (hash > 0)
                {
                    string persona = text.Substring(0, hash).Trim();
                    string occurrenceText = text.Substring(hash + 1).Trim();
                    if (persona.Length == 0 || occurrenceText.Length == 0 || !occurrenceText.All(Char.IsDigit)
                        || !Int32.TryParse(occurrenceText, out int occurrence) || occurrence <= 0)
                    {
                        selector.Malformed = true;
                        return selector;
                    }
                    selector.Persona = persona;
                    selector.Occurrence = occurrence;
                    return selector;
                }

                selector.Persona = text;
                return selector;
            }
        }

        private sealed class DefaultDroppedPayload
        {
            public string Entry { get; set; } = "";
            public string Reason { get; set; } = "";
            public string Confirmer { get; set; } = "";
        }

        private sealed class StageSkippedPayload
        {
            public string Persona { get; set; } = "";
            public int StageOrder { get; set; }
            public string Reason { get; set; } = "";
            public string Confirmer { get; set; } = "";
        }

        #endregion
    }
}
