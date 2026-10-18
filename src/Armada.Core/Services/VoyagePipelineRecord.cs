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
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The stage list a voyage was dispatched with. Every dispatch path records the pipeline it resolved,
    /// before operator stage skips, as one <c>voyage.pipeline_recorded</c> event on the voyage; the skips
    /// are recorded separately as <c>voyage.stage_skipped</c> events. A rescue anchors on and continues
    /// from this recorded list, so a pipeline edited, deleted or re-pointed while the voyage runs does not
    /// change which stages the rescue runs. A voyage with no recorded list (one dispatched before
    /// recording existed, or one whose record could not be read) is rescued from the live pipeline, and
    /// the rescue records a <c>rescue.live_pipeline_used</c> event that says so.
    /// </summary>
    public static class VoyagePipelineRecord
    {
        #region Public-Members

        /// <summary>Event type that records the stage list a voyage was dispatched with.</summary>
        public const string RecordedEventType = "voyage.pipeline_recorded";

        /// <summary>Event type recorded when a rescue uses the live pipeline because its voyage has no recorded list.</summary>
        public const string LiveFallbackEventType = "rescue.live_pipeline_used";

        /// <summary>How many of a voyage's newest events are searched for its record.</summary>
        public const int EventSearchLimit = 1000;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record the stage list a voyage was dispatched with. A null pipeline or one without stages
        /// records nothing. A failure to record is logged, never thrown into the dispatch that already
        /// created the voyage.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module, or null.</param>
        /// <param name="voyage">The created voyage.</param>
        /// <param name="pipeline">The pipeline the dispatch resolved, before stage skips.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task RecordAsync(
            DatabaseDriver database,
            LoggingModule? logging,
            Voyage voyage,
            Pipeline? pipeline,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (voyage == null || pipeline?.Stages == null || pipeline.Stages.Count == 0) return;

            try
            {
                ArmadaEvent evt = new ArmadaEvent(RecordedEventType,
                    "Voyage " + voyage.Id + " dispatched with pipeline " + pipeline.Name + " ("
                    + String.Join(" > ", pipeline.Stages.OrderBy(stage => stage.Order).Select(stage => stage.PersonaName + "(" + stage.Order + ")"))
                    + ").");
                evt.EntityType = "voyage";
                evt.EntityId = voyage.Id;
                evt.VoyageId = voyage.Id;
                evt.TenantId = voyage.TenantId;
                evt.UserId = voyage.UserId;
                evt.Payload = JsonSerializer.Serialize(new RecordedPipelinePayload
                {
                    PipelineId = pipeline.Id,
                    PipelineName = pipeline.Name,
                    Stages = pipeline.Stages.OrderBy(stage => stage.Order).ToList()
                });
                await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logging?.Warn("[VoyagePipelineRecord] could not record " + RecordedEventType + " for voyage "
                    + voyage.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Read the stage list a voyage was dispatched with.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="logging">Logging module, or null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The recorded pipeline, or null when the voyage has no readable record.</returns>
        public static async Task<Pipeline?> ReadAsync(
            DatabaseDriver database,
            string? voyageId,
            LoggingModule? logging,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(voyageId)) return null;

            List<ArmadaEvent> events = await database.Events
                .EnumerateByEntityAsync("voyage", voyageId, EventSearchLimit, token).ConfigureAwait(false);
            ArmadaEvent? recorded = events
                .Where(item => String.Equals(item.EventType, RecordedEventType, StringComparison.Ordinal))
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            if (recorded == null) return null;

            RecordedPipelinePayload? payload = null;
            try
            {
                if (!String.IsNullOrWhiteSpace(recorded.Payload))
                    payload = JsonSerializer.Deserialize<RecordedPipelinePayload>(recorded.Payload);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
            {
                logging?.Warn("[VoyagePipelineRecord] unreadable " + RecordedEventType + " event " + recorded.Id
                    + " on voyage " + voyageId + ": " + ex.Message);
                return null;
            }

            if (payload?.Stages == null || payload.Stages.Count == 0 || payload.Stages.Any(stage => stage == null))
            {
                logging?.Warn("[VoyagePipelineRecord] " + RecordedEventType + " event " + recorded.Id
                    + " on voyage " + voyageId + " holds no stages");
                return null;
            }

            Pipeline pipeline = new Pipeline
            {
                Name = String.IsNullOrWhiteSpace(payload.PipelineName) ? "recorded" : payload.PipelineName,
                Stages = payload.Stages.OrderBy(stage => stage.Order).ToList()
            };
            if (!String.IsNullOrWhiteSpace(payload.PipelineId)) pipeline.Id = payload.PipelineId;
            return pipeline;
        }

        /// <summary>
        /// Count the Open and InProgress voyages whose recorded stage list came from a pipeline. A voyage
        /// with no record is not counted, because which pipeline it used is not known.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="pipelineId">Pipeline identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The count.</returns>
        public static async Task<int> CountRunningVoyagesAsync(
            DatabaseDriver database,
            string pipelineId,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(pipelineId)) return 0;

            List<Voyage> running = new List<Voyage>();
            running.AddRange(await database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false));
            running.AddRange(await database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false));

            int count = 0;
            foreach (Voyage voyage in running)
            {
                Pipeline? recorded = await ReadAsync(database, voyage.Id, null, token).ConfigureAwait(false);
                if (recorded != null && String.Equals(recorded.Id, pipelineId, StringComparison.Ordinal)) count++;
            }

            return count;
        }

        #endregion

        #region Private-Members

        private sealed class RecordedPipelinePayload
        {
            public string? PipelineId { get; set; } = null;
            public string? PipelineName { get; set; } = null;
            public List<PipelineStage>? Stages { get; set; } = null;
        }

        #endregion
    }
}
