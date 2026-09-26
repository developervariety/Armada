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
    /// with a null dependency because its predecessor was skipped.
    /// </summary>
    public static class PipelineStageSkip
    {
        #region Public-Members

        /// <summary>Event type recorded once per skipped stage.</summary>
        public const string StageSkippedEventType = "voyage.stage_skipped";

        /// <summary>Refusal code: the request tried to skip the Judge.</summary>
        public const string JudgeRefusedCode = "stage_skip_judge_refused";

        /// <summary>Refusal code: a named persona is blank or is not a stage of the effective pipeline.</summary>
        public const string UnknownPersonaCode = "stage_skip_unknown_persona";

        /// <summary>Refusal code: the skip would leave no stage other than the Judge.</summary>
        public const string NoWorkRemainsCode = "stage_skip_leaves_no_work";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Refuse the parts of a skip request that need no pipeline to judge: a blank name or the Judge.
        /// </summary>
        /// <param name="request">Skip request, or null.</param>
        /// <exception cref="StageSkipRefusedException">When a name is blank or names the Judge.</exception>
        public static void ValidateNamesOrThrow(StageSkipRequest? request)
        {
            if (!StageSkipRequest.HasStages(request)) return;

            foreach (string? name in request!.Stages)
            {
                if (String.IsNullOrWhiteSpace(name))
                {
                    throw new StageSkipRefusedException(UnknownPersonaCode, name,
                        "skipStages contains a blank persona name.");
                }

                if (PersonaCatalog.Matches(name, PersonaCatalog.Judge))
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
                ConfirmedUtc = DateTime.UtcNow
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
        /// <exception cref="StageSkipRefusedException">
        /// When a name is blank, names the Judge, is not a stage of the pipeline, or the skip would
        /// leave no stage other than the Judge.
        /// </exception>
        public static PipelineStageSkipResult Apply(Pipeline? pipeline, StageSkipRequest? request)
        {
            if (!StageSkipRequest.HasStages(request))
                return new PipelineStageSkipResult(pipeline, new List<PipelineStage>());

            ValidateNamesOrThrow(request);

            List<PipelineStage> stages = (pipeline?.Stages ?? new List<PipelineStage> { new PipelineStage(1, PersonaCatalog.Worker) })
                .Where(stage => stage != null)
                .ToList();
            string pipelineName = pipeline?.Name ?? "single-stage Worker dispatch";

            foreach (string name in request!.Stages)
            {
                if (!stages.Any(stage => NamesStage(stage.PersonaName, name)))
                {
                    throw new StageSkipRefusedException(UnknownPersonaCode, name,
                        "skipStages names \"" + name + "\", which is not a stage of the effective pipeline ("
                        + pipelineName + "; stages: " + String.Join(", ", stages.Select(stage => stage.PersonaName)) + ").");
                }
            }

            List<PipelineStage> skipped = stages
                .Where(stage => request.Stages.Any(name => NamesStage(stage.PersonaName, name)))
                .ToList();
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
            return new PipelineStageSkipResult(effective, skipped);
        }

        /// <summary>
        /// Record one <c>voyage.stage_skipped</c> event per skipped stage, naming the persona, the
        /// reason and who confirmed the skip. A failure to record is logged, never thrown into the
        /// dispatch that already created the voyage.
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
            if (voyage == null || result == null || result.SkippedStages.Count == 0) return;

            string reason = String.IsNullOrWhiteSpace(request?.Reason) ? "operator-confirmed skip" : request!.Reason!.Trim();
            string confirmer = String.IsNullOrWhiteSpace(request?.ConfirmedBy) ? "unknown" : request!.ConfirmedBy!.Trim();

            foreach (PipelineStage stage in result.SkippedStages)
            {
                try
                {
                    ArmadaEvent evt = new ArmadaEvent(StageSkippedEventType,
                        "Stage " + stage.PersonaName + " skipped on voyage " + voyage.Id
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
