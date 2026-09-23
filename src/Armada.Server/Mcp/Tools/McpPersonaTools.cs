namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers MCP tools for persona CRUD operations.
    /// </summary>
    public static class McpPersonaTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers persona MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for persona data access.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database)
        {
            register(
                "create_persona",
                "Create a new persona with a name and prompt template reference",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Persona name (e.g. 'Worker', 'Architect', 'Judge')" },
                        description = new { type = "string", description = "Human-readable description of what this persona does" },
                        promptTemplateName = new { type = "string", description = "Prompt template name for this persona (references PromptTemplate.Name)" },
                        minimumTier = new { type = new[] { "string", "null" }, @enum = new[] { "Economy", "Standard", "Premium", null }, description = "Minimum capability tier; omit or pass null for no floor." },
                        defaultCaptainId = new { type = "string", description = "Captain id missions of this persona prefer. Omit or pass null or empty for none. Refused with default_captain_not_found for an id outside the caller's tenant and default_captain_persona_locked for a captain whose AllowedPersonas excludes the persona; a refused create writes nothing." },
                        defaultPlaybooks = DefaultPlaybooksSchema()
                    },
                    required = new[] { "name", "promptTemplateName" }
                },
                async (args) =>
                {
                    PersonaArgs request = JsonSerializer.Deserialize<PersonaArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrEmpty(request.Name)) return (object)new { Error = "name is required" };
                    if (String.IsNullOrEmpty(request.PromptTemplateName)) return (object)new { Error = "promptTemplateName is required" };

                    Persona persona = new Persona(request.Name, request.PromptTemplateName);
                    AuthContext caller = McpCallerContext.Require();
                    persona.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
                    persona.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
                    if (request.Description != null)
                        persona.Description = request.Description;
                    if (request.MinimumTierSupplied)
                        persona.MinimumTier = request.MinimumTier;
                    if (request.DefaultPlaybooks != null)
                        persona.DefaultPlaybooks = SerializeDefaultPlaybooks(request.DefaultPlaybooks);
                    PersonaRoutingUpdate routing = JsonSerializer.Deserialize<PersonaRoutingUpdate>(args!.Value, _JsonOptions) ?? new PersonaRoutingUpdate();
                    string? retiredFieldError = routing.RetiredFieldError();
                    if (retiredFieldError != null) return (object)new { Error = retiredFieldError, Code = PersonaRoutingUpdate.SpecialistRetiredErrorCode };
                    if (routing.DefaultCaptainIdSupplied)
                    {
                        string? defaultCaptainError = await Armada.Core.Services.PersonaDefaultCaptainRule.ApplyAsync(database, persona, routing.DefaultCaptainId).ConfigureAwait(false);
                        if (defaultCaptainError != null) return (object)new { Error = defaultCaptainError };
                    }
                    persona = await database.Personas.CreateAsync(persona).ConfigureAwait(false);

                    return (object)persona;
                });

            register(
                "get_persona",
                "Get a persona by name",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Persona name" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PersonaArgs request = JsonSerializer.Deserialize<PersonaArgs>(args!.Value, _JsonOptions)!;
                    string name = request.Name;
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };
                    Persona? persona = await ReadVisibleAsync(database, McpCallerContext.Require(), name).ConfigureAwait(false);
                    if (persona == null) return (object)new { Error = "Persona not found: " + name };
                    return (object)persona;
                });

            register(
                "update_persona",
                "Update an existing persona's properties",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Persona name (used to look up the persona)" },
                        description = new { type = "string", description = "New description" },
                        promptTemplateName = new { type = "string", description = "New prompt template name" },
                        minimumTier = new { type = new[] { "string", "null" }, @enum = new[] { "Economy", "Standard", "Premium", null }, description = "Minimum capability tier. Pass null to clear it; omit to leave it unchanged." },
                        defaultCaptainId = new { type = "string", description = "Captain id missions of this persona prefer. Null or empty clears it; omit to leave it unchanged. Refused with default_captain_not_found for an id outside the persona's tenant and default_captain_persona_locked for a captain whose AllowedPersonas excludes the persona." },
                        defaultPlaybooks = DefaultPlaybooksSchema()
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PersonaArgs request = JsonSerializer.Deserialize<PersonaArgs>(args!.Value, _JsonOptions)!;
                    string name = request.Name;
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };

                    AuthContext caller = McpCallerContext.Require();
                    Persona? persona = await ReadVisibleAsync(database, caller, name).ConfigureAwait(false);
                    if (persona == null || !Armada.Core.Authorization.OwnershipPolicy.CanEdit(caller, persona)) return (object)new { Error = "Persona not found: " + name };

                    if (request.Description != null)
                        persona.Description = request.Description;
                    if (request.PromptTemplateName != null)
                        persona.PromptTemplateName = request.PromptTemplateName;
                    if (request.MinimumTierSupplied)
                        persona.MinimumTier = request.MinimumTier;
                    if (request.DefaultPlaybooks != null)
                        persona.DefaultPlaybooks = SerializeDefaultPlaybooks(request.DefaultPlaybooks);
                    PersonaRoutingUpdate routing = JsonSerializer.Deserialize<PersonaRoutingUpdate>(args!.Value, _JsonOptions) ?? new PersonaRoutingUpdate();
                    string? retiredFieldError = routing.RetiredFieldError();
                    if (retiredFieldError != null) return (object)new { Error = retiredFieldError, Code = PersonaRoutingUpdate.SpecialistRetiredErrorCode };
                    if (routing.DefaultCaptainIdSupplied)
                    {
                        string? defaultCaptainError = await Armada.Core.Services.PersonaDefaultCaptainRule.ApplyAsync(database, persona, routing.DefaultCaptainId).ConfigureAwait(false);
                        if (defaultCaptainError != null) return (object)new { Error = defaultCaptainError };
                    }
                    persona.LastUpdateUtc = DateTime.UtcNow;
                    persona = await database.Personas.UpdateAsync(persona).ConfigureAwait(false);
                    return (object)persona;
                });

            register(
                "delete_persona",
                "Delete a persona by name. Built-in personas cannot be deleted.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Persona name" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PersonaArgs request = JsonSerializer.Deserialize<PersonaArgs>(args!.Value, _JsonOptions)!;
                    string name = request.Name;
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };

                    AuthContext caller = McpCallerContext.Require();
                    Persona? persona = await ReadVisibleAsync(database, caller, name).ConfigureAwait(false);
                    if (persona == null || !Armada.Core.Authorization.OwnershipPolicy.CanEdit(caller, persona)) return (object)new { Error = "Persona not found: " + name };
                    if (persona.IsBuiltIn) return (object)new { Error = "Cannot delete built-in persona: " + name };

                    await database.Personas.DeleteAsync(persona.Id).ConfigureAwait(false);
                    return (object)new { Status = "deleted", Name = name };
                });
        }

        private static Task<Persona?> ReadVisibleAsync(DatabaseDriver database, AuthContext caller, string name)
        {
            return Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                caller,
                name,
                (tenantId, personaName) => database.Personas.ReadByNameAsync(tenantId, personaName),
                () => database.Personas.EnumerateAsync(),
                record => record.Name);
        }

        private static object DefaultPlaybooksSchema()
        {
            return new
            {
                type = "array",
                description = "Default playbooks merged for this persona. Pass an empty array to clear them.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        playbookId = new { type = "string" },
                        deliveryMode = new { type = "string", description = "InlineFullContent, InstructionWithReference, or AttachIntoWorktree" }
                    },
                    required = new[] { "playbookId", "deliveryMode" }
                }
            };
        }

        private static string? SerializeDefaultPlaybooks(System.Collections.Generic.List<SelectedPlaybook> playbooks)
        {
            return playbooks.Count == 0 ? null : JsonSerializer.Serialize(playbooks, _JsonOptions);
        }
    }
}
