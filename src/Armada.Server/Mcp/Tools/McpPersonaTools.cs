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
    using Armada.Core.Services;
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
            PersonaService personas = new PersonaService(database);

            register(
                "create_persona",
                "Create a new persona with a name and prompt template reference",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Persona name (e.g. 'Worker', 'Architect', 'Judge'). A name already used in the caller's tenant is refused with code conflict." },
                        description = new { type = "string", description = "Human-readable description of what this persona does" },
                        promptTemplateName = new { type = "string", description = "Prompt template name for this persona (references PromptTemplate.Name)" },
                        minimumTier = new { type = new[] { "string", "null" }, @enum = new[] { "Economy", "Standard", "Premium", null }, description = "Minimum capability tier; omit or pass null for no floor." },
                        defaultCaptainId = new { type = "string", description = "Captain id missions of this persona prefer. Omit or pass null or empty for none. Refused with default_captain_not_found for an id outside the caller's tenant and default_captain_persona_locked for a captain whose AllowedPersonas excludes the persona; a refused create writes nothing." },
                        defaultPlaybooks = DefaultPlaybooksSchema(),
                        active = new { type = "boolean", description = "Whether the persona is active (default true)" },
                        ownershipScope = new { type = "string", @enum = new[] { "TenantWide", "UserSpecific" }, description = "Who may see the record inside its tenant. An administrator's choice is kept (TenantWide when omitted); any other caller's record is UserSpecific." }
                    },
                    required = new[] { "name", "promptTemplateName" }
                },
                async (args) =>
                {
                    PersonaWriteRequest request = JsonSerializer.Deserialize<PersonaWriteRequest>(args!.Value, _JsonOptions) ?? new PersonaWriteRequest();
                    return McpRecordWriteResult.From(await personas.CreateAsync(McpCallerContext.Require(), request).ConfigureAwait(false));
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
                    PersonaWriteRequest request = JsonSerializer.Deserialize<PersonaWriteRequest>(args!.Value, _JsonOptions) ?? new PersonaWriteRequest();
                    string name = request.Name ?? "";
                    if (String.IsNullOrEmpty(name)) return (object)new { Error = "name is required" };
                    Persona? persona = await personas.ReadVisibleAsync(McpCallerContext.Require(), name).ConfigureAwait(false);
                    if (persona == null) return (object)new { Error = "Persona not found: " + name };
                    return (object)persona;
                });

            register(
                "update_persona",
                "Update an existing persona's properties. Only supplied fields change.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Persona name (used to look up the persona)" },
                        description = new { type = "string", description = "New description. An empty string clears it.", emptyStringClears = true },
                        promptTemplateName = new { type = "string", description = "New prompt template name" },
                        minimumTier = new { type = new[] { "string", "null" }, @enum = new[] { "Economy", "Standard", "Premium", null }, description = "Minimum capability tier. Pass null to clear it; omit to leave it unchanged." },
                        defaultCaptainId = new { type = "string", description = "Captain id missions of this persona prefer. Null or empty clears it; omit to leave it unchanged. Refused with default_captain_not_found for an id outside the persona's tenant and default_captain_persona_locked for a captain whose AllowedPersonas excludes the persona.", emptyStringClears = true },
                        defaultPlaybooks = DefaultPlaybooksSchema(),
                        active = new { type = "boolean", description = "Whether the persona is active" }
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    PersonaWriteRequest request = JsonSerializer.Deserialize<PersonaWriteRequest>(args!.Value, _JsonOptions) ?? new PersonaWriteRequest();
                    request.OwnershipScope = null;
                    return McpRecordWriteResult.From(await personas.UpdateAsync(McpCallerContext.Require(), request.Name, request).ConfigureAwait(false));
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
                    PersonaWriteRequest request = JsonSerializer.Deserialize<PersonaWriteRequest>(args!.Value, _JsonOptions) ?? new PersonaWriteRequest();
                    RecordWriteResult<Persona> result = await personas.DeleteAsync(McpCallerContext.Require(), request.Name).ConfigureAwait(false);
                    if (!result.Succeeded) return McpRecordWriteResult.From(result);
                    return (object)new { Status = "deleted", Name = result.Record!.Name };
                });
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
    }
}
