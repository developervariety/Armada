namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers MCP tools for prompt template operations.
    /// </summary>
    public static class McpPromptTemplateTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers prompt template MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for prompt template data access.</param>
        /// <param name="templateService">Prompt template service for resolve and reset operations.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IPromptTemplateService templateService)
        {
            register(
                "list_prompt_templates",
                "List prompt templates, optionally filtered by category.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string", description = "Optional category filter (for example 'persona' or 'mission')" },
                        includeFullContent = new { type = "boolean", description = "Return long free-text fields whole instead of previewing them (default false). Previewed fields carry a companion <name>Length, and the response carries TruncatedFieldCount." }
                    }
                },
                async (args) =>
                {
                    PromptTemplateArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<PromptTemplateArgs>(args.Value, _JsonOptions) ?? new PromptTemplateArgs()
                        : new PromptTemplateArgs();
                    AuthContext caller = McpCallerContext.Require();
                    List<PromptTemplate> templates = (await templateService.ListAsync(request.Category).ConfigureAwait(false))
                        .FindAll(template => Armada.Core.Authorization.OwnershipPolicy.CanView(caller, template));
                    bool wantsFull = args.HasValue
                        && args.Value.TryGetProperty("includeFullContent", out JsonElement _full)
                        && _full.ValueKind == JsonValueKind.True;
                    return McpResultPreview.Apply(templates, wantsFull);
                });

            register(
                "create_prompt_template",
                "Create a new prompt template.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Template name (e.g. 'persona.worker.copy')" },
                        category = new { type = "string", description = "Template category such as 'persona' or 'mission'" },
                        content = new { type = "string", description = "Template content with {Placeholder} parameters" },
                        description = new { type = "string", description = "Human-readable description of the template" },
                        active = new { type = "boolean", description = "Whether the template should be active" },
                        ownershipScope = new { type = "string", @enum = new[] { "TenantWide", "UserSpecific" }, description = "Who may see the record inside its tenant. An administrator's choice is kept (TenantWide when omitted); any other caller's record is UserSpecific." }
                    },
                    required = new[] { "name", "category", "content" }
                },
                async (args) =>
                {
                    PromptTemplateWriteRequest request = JsonSerializer.Deserialize<PromptTemplateWriteRequest>(args!.Value, _JsonOptions) ?? new PromptTemplateWriteRequest();
                    return McpRecordWriteResult.From(await new PromptTemplateWriteService(database).CreateAsync(McpCallerContext.Require(), request).ConfigureAwait(false));
                });

            register(
                "get_prompt_template",
                "Get a prompt template by name. Resolves from database first, falls back to embedded defaults.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Template name (e.g. 'mission.rules', 'persona.worker')" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PromptTemplateArgs request = JsonSerializer.Deserialize<PromptTemplateArgs>(args!.Value, _JsonOptions)!;
                    string name = request.Name;
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };
                    // A stored record the caller may read wins; otherwise the shared resolution, which
                    // never returns another user's private template, supplies the embedded default.
                    AuthContext caller = McpCallerContext.Require();
                    PromptTemplate? template = await Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                        caller,
                        name,
                        (tenantId, templateName) => database.PromptTemplates.ReadByNameAsync(tenantId, templateName),
                        () => database.PromptTemplates.EnumerateAsync(),
                        record => record.Name).ConfigureAwait(false)
                        ?? await templateService.ResolveAsync(name).ConfigureAwait(false);
                    if (template == null || !Armada.Core.Authorization.OwnershipPolicy.CanView(caller, template)) return (object)new { Error = "Template not found: " + name };
                    return (object)template;
                });

            register(
                "update_prompt_template",
                "Update an existing prompt template. Only supplied fields change. A template that does not exist is refused with code not_found; create it with create_prompt_template.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Template name (e.g. 'mission.rules', 'persona.worker')" },
                        content = new { type = "string", description = "Template content with {Placeholder} parameters" },
                        description = new { type = "string", description = "Human-readable description of the template. An empty string clears it.", emptyStringClears = true },
                        category = new { type = "string", description = "Template category such as 'persona' or 'mission'" },
                        active = new { type = "boolean", description = "Whether the template is active" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PromptTemplateWriteRequest request = JsonSerializer.Deserialize<PromptTemplateWriteRequest>(args!.Value, _JsonOptions) ?? new PromptTemplateWriteRequest();
                    request.OwnershipScope = null;
                    return McpRecordWriteResult.From(await new PromptTemplateWriteService(database).UpdateAsync(McpCallerContext.Require(), request.Name, request).ConfigureAwait(false));
                });

            register(
                "reset_prompt_template",
                "Reset a prompt template to its embedded resource default content. Only works for built-in templates.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Template name to reset (e.g. 'mission.rules', 'persona.worker')" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PromptTemplateArgs request = JsonSerializer.Deserialize<PromptTemplateArgs>(args!.Value, _JsonOptions)!;
                    string name = request.Name;
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };
                    PromptTemplate? template = await templateService.ResetToDefaultAsync(name).ConfigureAwait(false);
                    if (template == null) return (object)new { Error = "No embedded default exists for template: " + name };
                    return (object)template;
                });
        }
    }
}
