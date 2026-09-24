namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one create and update rule for stored prompt templates. REST, MCP and WebSocket call it and only map
    /// its result. Names and categories are trimmed, a name is unique across the server because prompt
    /// resolution reads templates by name alone, and an update changes only an existing template: it never
    /// creates one. A template is found by name as the caller sees it and changed only when the ownership rule
    /// lets the caller edit it (a built-in template only by a global administrator).
    /// </summary>
    public class PromptTemplateWriteService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        public PromptTemplateWriteService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find a stored template by name as the caller sees it.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Template name.</param>
        /// <returns>The template, or null when absent or not visible.</returns>
        public Task<PromptTemplate?> ReadVisibleAsync(AuthContext caller, string? name)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return Task.FromResult<PromptTemplate?>(null);
            return OwnedRecordScope.ReadByNameAsync(
                caller,
                name!.Trim(),
                (tenantId, templateName) => _Database.PromptTemplates.ReadByNameAsync(tenantId, templateName),
                () => _Database.PromptTemplates.EnumerateAsync(),
                record => record.Name);
        }

        /// <summary>
        /// Create a template owned by the caller.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<PromptTemplate>> CreateAsync(AuthContext caller, PromptTemplateWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (request == null) request = new PromptTemplateWriteRequest();

            string name = (request.Name ?? "").Trim();
            string category = (request.Category ?? "").Trim();
            if (name.Length == 0) return RecordWriteResult<PromptTemplate>.Invalid("Prompt template name is required");
            if (category.Length == 0) return RecordWriteResult<PromptTemplate>.Invalid("Prompt template category is required");
            if (String.IsNullOrWhiteSpace(request.Content)) return RecordWriteResult<PromptTemplate>.Invalid("Prompt template content is required");

            if (await _Database.PromptTemplates.ReadByNameAsync(name, token).ConfigureAwait(false) != null)
                return RecordWriteResult<PromptTemplate>.Conflict("Prompt template already exists: " + name);

            PromptTemplate template = new PromptTemplate(name, request.Content!);
            template.Category = category;
            template.Description = NullIfBlank(request.Description);
            template.TenantId = OwnershipPolicy.TenantOf(caller);
            template.UserId = OwnershipPolicy.UserOf(caller);
            template.OwnershipScope = OwnershipPolicy.CreateScopeFor(caller, request.OwnershipScope);
            template.IsBuiltIn = false;
            if (request.Active.HasValue) template.Active = request.Active.Value;
            template.CreatedUtc = DateTime.UtcNow;
            template.LastUpdateUtc = template.CreatedUtc;

            PromptTemplate created = await _Database.PromptTemplates.CreateAsync(template, token).ConfigureAwait(false);
            return RecordWriteResult<PromptTemplate>.Success(created);
        }

        /// <summary>
        /// Update an existing template the caller may change. Only supplied fields change; a missing template is
        /// refused rather than created.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="name">Template name.</param>
        /// <param name="request">Requested fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result.</returns>
        public async Task<RecordWriteResult<PromptTemplate>> UpdateAsync(AuthContext caller, string? name, PromptTemplateWriteRequest? request, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(name)) return RecordWriteResult<PromptTemplate>.Invalid("name is required");
            if (request == null) request = new PromptTemplateWriteRequest();

            PromptTemplate? existing = await ReadVisibleAsync(caller, name).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<PromptTemplate>.NotFound("Prompt template not found: " + name!.Trim() + ". Create it with create_prompt_template or POST /api/v1/prompt-templates.");
            if (!OwnershipPolicy.CanEdit(caller, existing))
            {
                return RecordWriteResult<PromptTemplate>.Forbidden(existing.IsBuiltIn
                    ? "Built-in prompt templates can be changed only by a global administrator"
                    : "You may not change this prompt template");
            }

            if (request.Content != null)
            {
                if (String.IsNullOrWhiteSpace(request.Content)) return RecordWriteResult<PromptTemplate>.Invalid("Prompt template content must not be empty");
                existing.Content = request.Content;
            }

            if (request.Category != null)
            {
                string category = request.Category.Trim();
                if (category.Length == 0) return RecordWriteResult<PromptTemplate>.Invalid("Prompt template category must not be empty");
                existing.Category = category;
            }

            if (request.Description != null) existing.Description = NullIfBlank(request.Description);
            if (request.Active.HasValue) existing.Active = request.Active.Value;
            existing.LastUpdateUtc = DateTime.UtcNow;

            PromptTemplate updated = await _Database.PromptTemplates.UpdateAsync(existing, token).ConfigureAwait(false);
            return RecordWriteResult<PromptTemplate>.Success(updated);
        }

        #endregion

        #region Private-Methods

        private static string? NullIfBlank(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        #endregion
    }
}
