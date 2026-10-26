namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class FleetActionService
    {
        /// <summary>Create an owner-scoped mission action.</summary>
        public async Task<FleetAction> CreateAsync(AuthContext caller, FleetActionUpsertRequest request, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            ValidateDefinition(request);
            FleetAction action = new FleetAction
            {
                TenantId = actor.TenantId!, UserId = actor.UserId!, Name = request.Name.Trim(),
                Active = request.Active, Kind = FleetActionKindEnum.Mission, PromptTemplate = request.PromptTemplate,
                PipelineId = Normalize(request.PipelineId), Concurrency = request.Concurrency,
                CreatedUtc = DateTime.UtcNow, LastUpdateUtc = DateTime.UtcNow
            };
            return await _Database.FleetActions.CreateAsync(action, token).ConfigureAwait(false);
        }

        /// <summary>Update one exact-owner mission action.</summary>
        public async Task<FleetAction> UpdateAsync(AuthContext caller, string actionId, FleetActionUpsertRequest request, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            ValidateDefinition(request);
            FleetAction? action = await _Database.FleetActions.ReadAsync(actor.TenantId!, actor.UserId!, actionId, token).ConfigureAwait(false);
            if (action == null) throw new KeyNotFoundException("Fleet action was not found.");
            action.Name = request.Name.Trim();
            action.Active = request.Active;
            action.PromptTemplate = request.PromptTemplate;
            action.PipelineId = Normalize(request.PipelineId);
            action.Concurrency = request.Concurrency;
            action.LastUpdateUtc = DateTime.UtcNow;
            if (!await _Database.FleetActions.UpdateAsync(action, token).ConfigureAwait(false))
                throw new KeyNotFoundException("Fleet action was not found.");
            return action;
        }

        /// <summary>Delete one exact-owner action definition without deleting its saved runs.</summary>
        public async Task<bool> DeleteAsync(AuthContext caller, string actionId, CancellationToken token = default)
        {
            AuthContext actor = await ValidateOperatorAsync(caller, token).ConfigureAwait(false);
            if (!await _Database.FleetActions.DeleteAsync(actor.TenantId!, actor.UserId!, actionId, token).ConfigureAwait(false))
                throw new KeyNotFoundException("Fleet action was not found.");
            return true;
        }

        private static void ValidateDefinition(FleetActionUpsertRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 255)
                throw new ArgumentException("A name of at most 255 characters is required.", nameof(request));
            if (String.IsNullOrWhiteSpace(request.PromptTemplate) || request.PromptTemplate.Length > _MaximumPromptLength)
                throw new ArgumentException("A prompt template of at most 16000 characters is required.", nameof(request));
            if (request.Concurrency < 1 || request.Concurrency > 8)
                throw new ArgumentException("Concurrency must be between one and eight.", nameof(request));
            if (!String.IsNullOrWhiteSpace(request.PipelineId) && request.PipelineId.Trim().Length > 128)
                throw new ArgumentException("The pipeline identifier is too long.", nameof(request));
        }

        private static string? Normalize(string? value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
