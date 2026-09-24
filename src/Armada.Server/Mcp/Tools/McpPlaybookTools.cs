namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Registers MCP tools for playbook management.
    /// </summary>
    public static class McpPlaybookTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers playbook MCP tools.
        /// </summary>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, LoggingModule logging)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (logging == null) throw new ArgumentNullException(nameof(logging));

            register(
                "get_playbook",
                "Get a playbook by ID.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Playbook ID (pbk_ prefix)" }
                    },
                    required = new[] { "id" }
                },
                async (args) =>
                {
                    PlaybookArgs request = JsonSerializer.Deserialize<PlaybookArgs>(args!.Value, _JsonOptions)!;
                    string id = request.Id?.Trim() ?? String.Empty;
                    if (String.IsNullOrWhiteSpace(id)) return (object)new { Error = "id is required" };
                    Playbook? playbook = await new PlaybookService(database, logging).ReadForCallerAsync(McpCallerContext.Require(), id).ConfigureAwait(false);
                    if (playbook == null) return (object)new { Error = "Playbook not found: " + id };
                    return (object)playbook;
                });

            register(
                "create_playbook",
                "Create a new markdown playbook.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fileName = new { type = "string", description = "Markdown filename (must end with .md)" },
                        description = new { type = "string", description = "Optional human-readable description" },
                        content = new { type = "string", description = "Markdown content" },
                        active = new { type = "boolean", description = "Whether the playbook is active" }
                    },
                    required = new[] { "fileName", "content" }
                },
                async (args) =>
                {
                    PlaybookWriteRequest request = JsonSerializer.Deserialize<PlaybookWriteRequest>(args!.Value, _JsonOptions) ?? new PlaybookWriteRequest();
                    return McpRecordWriteResult.From(await new PlaybookService(database, logging).CreateAsync(McpCallerContext.Require(), request).ConfigureAwait(false));
                });

            register(
                "update_playbook",
                "Update an existing playbook. Only supplied fields change.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Playbook ID (pbk_ prefix)" },
                        fileName = new { type = "string", description = "Markdown filename (must end with .md)" },
                        description = new { type = "string", description = "Human-readable description. An empty string clears it.", emptyStringClears = true },
                        content = new { type = "string", description = "Markdown content" },
                        active = new { type = "boolean", description = "Whether the playbook is active" }
                    },
                    required = new[] { "id" }
                },
                async (args) =>
                {
                    PlaybookArgs target = JsonSerializer.Deserialize<PlaybookArgs>(args!.Value, _JsonOptions)!;
                    PlaybookWriteRequest request = JsonSerializer.Deserialize<PlaybookWriteRequest>(args!.Value, _JsonOptions) ?? new PlaybookWriteRequest();
                    return McpRecordWriteResult.From(await new PlaybookService(database, logging).UpdateAsync(McpCallerContext.Require(), target.Id, request).ConfigureAwait(false));
                });

            register(
                "delete_playbook",
                "Delete a playbook by ID. Existing mission snapshots remain immutable.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Playbook ID (pbk_ prefix)" }
                    },
                    required = new[] { "id" }
                },
                async (args) =>
                {
                    PlaybookArgs request = JsonSerializer.Deserialize<PlaybookArgs>(args!.Value, _JsonOptions)!;
                    RecordWriteResult<Playbook> result = await new PlaybookService(database, logging).DeleteAsync(McpCallerContext.Require(), request.Id).ConfigureAwait(false);
                    if (!result.Succeeded) return McpRecordWriteResult.From(result);
                    return (object)new { Status = "deleted", PlaybookId = result.Record!.Id };
                });
        }
    }
}
