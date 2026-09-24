namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one create, update and delete rule for personas. REST, MCP and WebSocket call it and only map its
    /// result, so a field is validated, defaulted and allow-listed the same way on every surface.
    ///
    /// A persona is found by name as the caller sees it (<see cref="OwnedRecordScope.ReadByNameAsync{T}"/>):
    /// a record the caller may not read reads as absent, and one it may read but not change is refused as
    /// forbidden. A default captain passes <see cref="PersonaDefaultCaptainRule"/>. The retired specialist
    /// flag is refused and nothing is written.
    /// </summary>
    public class PersonaService
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _PlaybookJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        public PersonaService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find a persona by name as the caller sees it.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Persona name.</param>
        /// <returns>The persona, or null when absent or not visible.</returns>
        public Task<Persona?> ReadVisibleAsync(AuthContext caller, string? name)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return Task.FromResult<Persona?>(null);
            return OwnedRecordScope.ReadByNameAsync(
                caller,
                name!,
                (tenantId, personaName) => _Database.Personas.ReadByNameAsync(tenantId, personaName),
                () => _Database.Personas.EnumerateAsync(),
                record => record.Name);
        }

        /// <summary>
        /// Create a persona owned by the caller.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<Persona>> CreateAsync(AuthContext caller, PersonaWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (request == null) request = new PersonaWriteRequest();

            string? retired = request.RetiredFieldError();
            if (retired != null) return RecordWriteResult<Persona>.Invalid(retired, PersonaWriteRequest.SpecialistRetiredErrorCode);

            string name = (request.Name ?? "").Trim();
            if (name.Length == 0) return RecordWriteResult<Persona>.Invalid("name is required");
            string promptTemplateName = (request.PromptTemplateName ?? "").Trim();
            if (promptTemplateName.Length == 0) return RecordWriteResult<Persona>.Invalid("promptTemplateName is required");

            string tenantId = OwnershipPolicy.TenantOf(caller);
            if (await _Database.Personas.ReadByNameAsync(tenantId, name, token).ConfigureAwait(false) != null)
                return RecordWriteResult<Persona>.Conflict("Persona already exists: " + name);

            Persona persona = new Persona(name, promptTemplateName);
            persona.TenantId = tenantId;
            persona.UserId = OwnershipPolicy.UserOf(caller);
            persona.OwnershipScope = OwnershipPolicy.CreateScopeFor(caller, request.OwnershipScope);
            persona.IsBuiltIn = false;
            persona.Description = NullIfEmpty(request.Description);
            if (request.MinimumTierSupplied) persona.MinimumTier = request.MinimumTier;
            if (request.DefaultPlaybooks != null) persona.DefaultPlaybooks = SerializePlaybooks(request.DefaultPlaybooks);
            if (request.Active.HasValue) persona.Active = request.Active.Value;

            if (request.DefaultCaptainIdSupplied)
            {
                string? defaultCaptainError = await PersonaDefaultCaptainRule.ApplyAsync(_Database, persona, request.DefaultCaptainId, token).ConfigureAwait(false);
                if (defaultCaptainError != null) return RecordWriteResult<Persona>.Invalid(defaultCaptainError);
            }

            persona = await _Database.Personas.CreateAsync(persona, token).ConfigureAwait(false);
            return RecordWriteResult<Persona>.Success(persona);
        }

        /// <summary>
        /// Update a persona the caller may change. Only supplied fields change.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Persona name.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<Persona>> UpdateAsync(AuthContext caller, string? name, PersonaWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return RecordWriteResult<Persona>.Invalid("name is required");
            if (request == null) request = new PersonaWriteRequest();

            Persona? existing = await ReadVisibleAsync(caller, name).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<Persona>.NotFound("Persona not found: " + name);
            if (!OwnershipPolicy.CanEdit(caller, existing))
            {
                return RecordWriteResult<Persona>.Forbidden(existing.IsBuiltIn
                    ? "Built-in personas can be changed only by a global administrator"
                    : "You may not change this persona");
            }

            string? retired = request.RetiredFieldError();
            if (retired != null) return RecordWriteResult<Persona>.Invalid(retired, PersonaWriteRequest.SpecialistRetiredErrorCode);

            if (request.PromptTemplateName != null)
            {
                string promptTemplateName = request.PromptTemplateName.Trim();
                if (promptTemplateName.Length == 0) return RecordWriteResult<Persona>.Invalid("promptTemplateName must not be empty");
                existing.PromptTemplateName = promptTemplateName;
            }

            if (request.Description != null) existing.Description = NullIfEmpty(request.Description);
            if (request.MinimumTierSupplied) existing.MinimumTier = request.MinimumTier;
            if (request.DefaultPlaybooks != null) existing.DefaultPlaybooks = SerializePlaybooks(request.DefaultPlaybooks);
            if (request.Active.HasValue) existing.Active = request.Active.Value;

            if (request.DefaultCaptainIdSupplied)
            {
                string? defaultCaptainError = await PersonaDefaultCaptainRule.ApplyAsync(_Database, existing, request.DefaultCaptainId, token).ConfigureAwait(false);
                if (defaultCaptainError != null) return RecordWriteResult<Persona>.Invalid(defaultCaptainError);
            }

            existing.LastUpdateUtc = DateTime.UtcNow;
            Persona updated = await _Database.Personas.UpdateAsync(existing, token).ConfigureAwait(false);
            return RecordWriteResult<Persona>.Success(updated);
        }

        /// <summary>
        /// Delete a persona the caller may change. A built-in persona is never deleted.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Persona name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result carrying the deleted persona.</returns>
        public async Task<RecordWriteResult<Persona>> DeleteAsync(AuthContext caller, string? name, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return RecordWriteResult<Persona>.Invalid("name is required");

            Persona? existing = await ReadVisibleAsync(caller, name).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<Persona>.NotFound("Persona not found: " + name);
            if (existing.IsBuiltIn) return RecordWriteResult<Persona>.Invalid("Built-in personas cannot be deleted");
            if (!OwnershipPolicy.CanEdit(caller, existing)) return RecordWriteResult<Persona>.Forbidden("You may not delete this persona");

            await _Database.Personas.DeleteAsync(existing.Id, token).ConfigureAwait(false);
            return RecordWriteResult<Persona>.Success(existing);
        }

        #endregion

        #region Private-Methods

        private static string? SerializePlaybooks(List<SelectedPlaybook> playbooks)
        {
            return playbooks.Count == 0 ? null : JsonSerializer.Serialize(playbooks, _PlaybookJsonOptions);
        }

        private static string? NullIfEmpty(string? value)
        {
            return String.IsNullOrEmpty(value) ? null : value;
        }

        #endregion
    }
}
