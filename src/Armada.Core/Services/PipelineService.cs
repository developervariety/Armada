namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one create, update and delete rule for pipelines. REST, MCP and WebSocket call it and only map
    /// its result, so a field is validated, defaulted and allow-listed the same way on every surface.
    ///
    /// A pipeline is found by name as the caller sees it (<see cref="OwnedRecordScope.ReadByNameAsync{T}"/>):
    /// a record the caller may not read reads as absent, and one it may read but not change is refused as
    /// forbidden. A stage list that gives no order is stored in list order 1..n; one that gives every stage
    /// a positive order keeps it, so stages that share an order run as parallel siblings; a list that orders
    /// only some stages is refused. A stage list on update replaces the stages, and a stage
    /// field the request leaves out keeps the value of the existing stage for the same persona, so a
    /// caller that does not name the review gate never turns it off.
    /// </summary>
    public class PipelineService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        public PipelineService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find a pipeline by name as the caller sees it.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Pipeline name.</param>
        /// <returns>The pipeline, or null when absent or not visible.</returns>
        public Task<Pipeline?> ReadVisibleAsync(AuthContext caller, string? name)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return Task.FromResult<Pipeline?>(null);
            return OwnedRecordScope.ReadByNameAsync(
                caller,
                name!,
                (tenantId, pipelineName) => _Database.Pipelines.ReadByNameAsync(tenantId, pipelineName),
                () => _Database.Pipelines.EnumerateAsync(),
                record => record.Name);
        }

        /// <summary>
        /// Create a pipeline owned by the caller.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<Pipeline>> CreateAsync(AuthContext caller, PipelineWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (request == null) return RecordWriteResult<Pipeline>.Invalid("name is required");

            string name = (request.Name ?? "").Trim();
            if (name.Length == 0) return RecordWriteResult<Pipeline>.Invalid("name is required");
            if (request.Stages == null || request.Stages.Count == 0) return RecordWriteResult<Pipeline>.Invalid("stages is required and must not be empty");

            string tenantId = OwnershipPolicy.TenantOf(caller);
            if (await _Database.Pipelines.ReadByNameAsync(tenantId, name, token).ConfigureAwait(false) != null)
                return RecordWriteResult<Pipeline>.Conflict("Pipeline already exists: " + name);

            Pipeline pipeline = new Pipeline(name);
            pipeline.TenantId = tenantId;
            pipeline.UserId = OwnershipPolicy.UserOf(caller);
            pipeline.OwnershipScope = OwnershipPolicy.CreateScopeFor(caller, request.OwnershipScope);
            pipeline.IsBuiltIn = false;
            pipeline.Description = request.Description;
            if (request.Active.HasValue) pipeline.Active = request.Active.Value;

            string? stageError = BuildStages(request.Stages, null, pipeline.Id, out List<PipelineStage> stages);
            if (stageError != null) return RecordWriteResult<Pipeline>.Invalid(stageError);
            pipeline.Stages = stages;

            pipeline = await _Database.Pipelines.CreateAsync(pipeline, token).ConfigureAwait(false);
            return RecordWriteResult<Pipeline>.Success(pipeline);
        }

        /// <summary>
        /// Update a pipeline the caller may change. Only supplied fields change.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Pipeline name.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<Pipeline>> UpdateAsync(AuthContext caller, string? name, PipelineWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return RecordWriteResult<Pipeline>.Invalid("name is required");
            if (request == null) request = new PipelineWriteRequest();

            Pipeline? existing = await ReadVisibleAsync(caller, name).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<Pipeline>.NotFound("Pipeline not found: " + name);
            if (!OwnershipPolicy.CanEdit(caller, existing))
            {
                return RecordWriteResult<Pipeline>.Forbidden(existing.IsBuiltIn
                    ? "Built-in pipelines can be changed only by a global administrator"
                    : "You may not change this pipeline");
            }

            if (request.Stages != null)
            {
                if (request.Stages.Count == 0) return RecordWriteResult<Pipeline>.Invalid("stages must not be empty; omit stages to keep them");
                string? stageError = BuildStages(request.Stages, existing.Stages, existing.Id, out List<PipelineStage> stages);
                if (stageError != null) return RecordWriteResult<Pipeline>.Invalid(stageError);
                existing.Stages = stages;
            }

            if (request.Description != null) existing.Description = request.Description;
            if (request.Active.HasValue) existing.Active = request.Active.Value;
            existing.LastUpdateUtc = DateTime.UtcNow;

            Pipeline updated = await _Database.Pipelines.UpdateAsync(existing, token).ConfigureAwait(false);
            return RecordWriteResult<Pipeline>.Success(updated);
        }

        /// <summary>
        /// Delete a pipeline the caller may change. A built-in pipeline is never deleted.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Pipeline name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result carrying the deleted pipeline.</returns>
        public async Task<RecordWriteResult<Pipeline>> DeleteAsync(AuthContext caller, string? name, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return RecordWriteResult<Pipeline>.Invalid("name is required");

            Pipeline? existing = await ReadVisibleAsync(caller, name).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<Pipeline>.NotFound("Pipeline not found: " + name);
            if (existing.IsBuiltIn) return RecordWriteResult<Pipeline>.Invalid("Built-in pipelines cannot be deleted");
            if (!OwnershipPolicy.CanEdit(caller, existing)) return RecordWriteResult<Pipeline>.Forbidden("You may not delete this pipeline");

            await _Database.Pipelines.DeleteAsync(existing.Id, token).ConfigureAwait(false);
            return RecordWriteResult<Pipeline>.Success(existing);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Build the stored stages from a request list. A field the request leaves out takes
        /// the value of the existing stage for the same persona (matched in order of occurrence), or the
        /// model default when there is none.
        /// </summary>
        private static string? BuildStages(
            List<PipelineStageWriteRequest> requested,
            List<PipelineStage>? existingStages,
            string pipelineId,
            out List<PipelineStage> stages)
        {
            stages = new List<PipelineStage>();

            bool anyOrdered = requested.Any(stage => stage != null && stage.Order.HasValue);
            bool allOrdered = requested.All(stage => stage != null && stage.Order.HasValue && stage.Order.Value > 0);
            if (anyOrdered && !allOrdered) return "give every stage a positive order, or give none to use list order";

            // Explicit orders are kept, ties included (same-order stages are parallel siblings); OrderBy is stable.
            List<PipelineStageWriteRequest> ordered = allOrdered
                ? requested.OrderBy(stage => stage.Order!.Value).ToList()
                : requested.ToList();

            List<PipelineStage> unmatched = (existingStages ?? new List<PipelineStage>())
                .OrderBy(stage => stage.Order)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                PipelineStageWriteRequest? entry = ordered[i];
                string personaName = (entry?.PersonaName ?? "").Trim();
                if (entry == null || personaName.Length == 0) return "personaName is required for stage " + (i + 1);

                PipelineStage? previous = unmatched.FirstOrDefault(stage => String.Equals(stage.PersonaName, personaName, StringComparison.Ordinal));
                if (previous != null) unmatched.Remove(previous);

                PipelineStage stage = new PipelineStage(allOrdered ? entry.Order!.Value : i + 1, personaName);
                stage.PipelineId = pipelineId;
                stage.IsOptional = entry.IsOptional ?? previous?.IsOptional ?? false;
                stage.RequiresReview = entry.RequiresReview ?? previous?.RequiresReview ?? false;
                stage.ReviewDenyAction = entry.ReviewDenyAction ?? previous?.ReviewDenyAction ?? stage.ReviewDenyAction;
                stage.Description = entry.DescriptionSupplied ? entry.Description : previous?.Description;
                stage.PreferredModel = entry.PreferredModelSupplied ? NullIfBlank(entry.PreferredModel) : previous?.PreferredModel;
                stages.Add(stage);
            }

            return null;
        }

        private static string? NullIfBlank(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        #endregion
    }
}
