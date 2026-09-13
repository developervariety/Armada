namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Registers the native captain memory tools: search, get, create (or update by key), update and
    /// delete. Captains search before they write, so a repeated finding updates one record instead of
    /// scattering copies.
    /// </summary>
    public static class McpMemoryTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// The context memory tools act in. The MCP surface carries no per-request identity, so it acts
        /// as an administrator of the default tenant: it reaches every record of that tenant and no
        /// record of another tenant.
        /// </summary>
        /// <returns>The caller context.</returns>
        public static AuthContext CallerContext()
        {
            return AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "mcp");
        }

        /// <summary>
        /// Register the memory tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Optional logging module.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, LoggingModule? logging = null)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));

            MemoryService service = new MemoryService(database, logging);

            register(
                "search_memory",
                "Search native captain memory (episodic, semantic, procedural). Use this before writing, to find a record to correct instead of creating a duplicate. Returns the highest salience first, then the newest.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        search = new { type = "string", description = "Case-insensitive substring over content, summary, topic, key and tags" },
                        type = new { type = "string", description = "Filter by type: Episodic, Semantic, or Procedural" },
                        topic = new { type = "string", description = "Filter by exact topic" },
                        vesselId = new { type = "string", description = "Filter by the vessel a record is about or came from" },
                        pageNumber = new { type = "integer", description = "Page number, one based" },
                        pageSize = new { type = "integer", description = "Records per page" }
                    }
                },
                async (args) =>
                {
                    MemorySearchArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<MemorySearchArgs>(args.Value, _JsonOptions)!
                        : new MemorySearchArgs();

                    EnumerationQuery query = new EnumerationQuery();
                    if (request.PageNumber.HasValue) query.PageNumber = request.PageNumber.Value;
                    if (request.PageSize.HasValue) query.PageSize = request.PageSize.Value;
                    if (!String.IsNullOrWhiteSpace(request.VesselId)) query.VesselId = request.VesselId;

                    if (!TryParseType(request.Type, out MemoryTypeEnum? type)) return Failure("invalid", "Unknown memory type: " + request.Type);

                    return await GuardAsync(async () =>
                    {
                        EnumerationResult<Memory> result = await service.EnumerateAsync(CallerContext(), query, request.Search, type, request.Topic).ConfigureAwait(false);
                        return (object)result;
                    }).ConfigureAwait(false);
                });

            register(
                "get_memory",
                "Read one memory record by identifier, including its full content.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        memoryId = new { type = "string", description = "Memory identifier (mem_ prefix)" }
                    },
                    required = new[] { "memoryId" }
                },
                async (args) =>
                {
                    MemoryIdArgs request = JsonSerializer.Deserialize<MemoryIdArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.MemoryId)) return Failure("invalid", "memoryId is required");

                    return await GuardAsync(async () =>
                    {
                        Memory? memory = await service.ReadAsync(CallerContext(), request.MemoryId).ConfigureAwait(false);
                        if (memory == null) return Failure("not_found", "Memory not found: " + request.MemoryId);
                        return (object)memory;
                    }).ConfigureAwait(false);
                });

            register(
                "create_memory",
                "Record a durable finding, or update the record that already carries the same key. Classify it as Episodic (what happened), Semantic (a standalone fact), or Procedural (how to do something). Working memory is never stored. A rule from the shared external memory in your brief wins over a native record on conflict.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        type = new { type = "string", description = "Episodic, Semantic, or Procedural. Default Semantic" },
                        topic = new { type = "string", description = "Grouping inside the type, for example 'build'" },
                        key = new { type = "string", description = "Stable slug. Writing the same key again updates that record in place" },
                        summary = new { type = "string", description = "One-line recall hook" },
                        content = new { type = "string", description = "The memory itself, written to stand on its own" },
                        salience = new { type = "number", description = "Importance 0.0 to 1.0. Default 0.5" },
                        tags = new { type = "array", items = new { type = "string" }, description = "Lowercase slug tags for retrieval" },
                        sourceKind = new { type = "string", description = "Voyage, Mission, Vessel, Conversation, Manual, or Other" },
                        sourceVoyageId = new { type = "string", description = "Originating voyage identifier" },
                        sourceMissionId = new { type = "string", description = "Originating mission identifier" },
                        sourceVesselId = new { type = "string", description = "Originating vessel identifier" },
                        sourceDetail = new { type = "string", description = "Free-text note on where this came from" },
                        vesselId = new { type = "string", description = "Vessel this record is about" },
                        scope = new { type = "string", description = "TenantWide or UserSpecific" },
                        expectedVersion = new { type = "integer", description = "Version you read, to refuse an overwrite of a newer record" }
                    },
                    required = new[] { "content" }
                },
                async (args) =>
                {
                    MemoryUpsertArgs request = JsonSerializer.Deserialize<MemoryUpsertArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.Content)) return Failure("invalid", "content is required");
                    if (!TryParseType(request.Type, out MemoryTypeEnum? type)) return Failure("invalid", "Unknown memory type: " + request.Type);
                    if (!TryParse(request.SourceKind, out MemorySourceKindEnum? sourceKind)) return Failure("invalid", "Unknown source kind: " + request.SourceKind);
                    if (!TryParse(request.Scope, out MemoryScopeEnum? scope)) return Failure("invalid", "Unknown scope: " + request.Scope);

                    Memory memory = new Memory();
                    memory.Type = type ?? MemoryTypeEnum.Semantic;
                    memory.Topic = request.Topic;
                    memory.Key = request.Key;
                    memory.Summary = request.Summary;
                    memory.Content = request.Content;
                    if (request.Salience.HasValue) memory.Salience = request.Salience.Value;
                    memory.Tags = request.Tags ?? new List<string>();
                    memory.SourceKind = sourceKind ?? MemorySourceKindEnum.Manual;
                    memory.SourceVoyageId = request.SourceVoyageId;
                    memory.SourceMissionId = request.SourceMissionId;
                    memory.SourceVesselId = request.SourceVesselId;
                    memory.SourceDetail = request.SourceDetail;
                    memory.VesselId = request.VesselId;
                    if (scope.HasValue) memory.Scope = scope.Value;

                    return await GuardAsync(async () =>
                    {
                        Memory saved = await service.UpsertAsync(CallerContext(), memory, request.ExpectedVersion).ConfigureAwait(false);
                        return (object)saved;
                    }).ConfigureAwait(false);
                });

            register(
                "update_memory",
                "Correct one memory record. Only the fields you supply change, and the record's version increases. Use this instead of creating a second record on the same subject.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        memoryId = new { type = "string", description = "Memory identifier (mem_ prefix)" },
                        type = new { type = "string", description = "New type: Episodic, Semantic, or Procedural" },
                        topic = new { type = "string", description = "New topic" },
                        key = new { type = "string", description = "New key" },
                        summary = new { type = "string", description = "New summary" },
                        content = new { type = "string", description = "New content" },
                        salience = new { type = "number", description = "New salience 0.0 to 1.0" },
                        tags = new { type = "array", items = new { type = "string" }, description = "Replacement tag list" },
                        vesselId = new { type = "string", description = "New vessel association" },
                        sourceDetail = new { type = "string", description = "New provenance note" },
                        scope = new { type = "string", description = "New scope: TenantWide or UserSpecific" },
                        expectedVersion = new { type = "integer", description = "Version you read, to refuse an overwrite of a newer record" }
                    },
                    required = new[] { "memoryId" }
                },
                async (args) =>
                {
                    MemoryUpdateArgs request = JsonSerializer.Deserialize<MemoryUpdateArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.MemoryId)) return Failure("invalid", "memoryId is required");

                    MemoryUpdate update = new MemoryUpdate();
                    update.Type = request.Type;
                    update.Topic = request.Topic;
                    update.Key = request.Key;
                    update.Summary = request.Summary;
                    update.Content = request.Content;
                    update.Salience = request.Salience;
                    update.Tags = request.Tags;
                    update.VesselId = request.VesselId;
                    update.SourceDetail = request.SourceDetail;
                    update.Scope = request.Scope;
                    update.ExpectedVersion = request.ExpectedVersion;

                    return await GuardAsync(async () =>
                    {
                        Memory saved = await service.UpdateAsync(CallerContext(), request.MemoryId, update).ConfigureAwait(false);
                        return (object)saved;
                    }).ConfigureAwait(false);
                });

            register(
                "delete_memory",
                "Delete one memory record that has gone stale or wrong. A wrong record is worse than a missing one.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        memoryId = new { type = "string", description = "Memory identifier (mem_ prefix)" }
                    },
                    required = new[] { "memoryId" }
                },
                async (args) =>
                {
                    MemoryIdArgs request = JsonSerializer.Deserialize<MemoryIdArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.MemoryId)) return Failure("invalid", "memoryId is required");

                    return await GuardAsync(async () =>
                    {
                        await service.DeleteAsync(CallerContext(), request.MemoryId).ConfigureAwait(false);
                        return (object)new { Status = "deleted", MemoryId = request.MemoryId };
                    }).ConfigureAwait(false);
                });
        }

        private static async Task<object> GuardAsync(Func<Task<object>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (MemoryConflictException conflict)
            {
                return Failure("conflict", conflict.Message, conflict.Kind);
            }
            catch (KeyNotFoundException notFound)
            {
                return Failure("not_found", notFound.Message);
            }
            catch (UnauthorizedAccessException denied)
            {
                return Failure("forbidden", denied.Message);
            }
            catch (ArgumentException invalid)
            {
                return Failure("invalid", invalid.Message);
            }
        }

        private static object Failure(string code, string message, string? kind = null)
        {
            return new { Error = message, Code = code, Conflict = kind };
        }

        private static bool TryParseType(string? value, out MemoryTypeEnum? parsed)
        {
            return TryParse(value, out parsed);
        }

        private static bool TryParse<TEnum>(string? value, out TEnum? parsed) where TEnum : struct
        {
            parsed = null;
            if (String.IsNullOrWhiteSpace(value)) return true;
            if (!Enum.TryParse<TEnum>(value.Trim(), true, out TEnum found)) return false;
            parsed = found;
            return true;
        }
    }
}
