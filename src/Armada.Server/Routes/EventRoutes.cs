namespace Armada.Server.Routes
{
    using System.Diagnostics;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for event management.
    /// </summary>
    public class EventRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task> _emitEvent;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="emitEvent">Event broadcast callback.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public EventRoutes(
            DatabaseDriver database,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent,
            JsonSerializerOptions jsonOptions)
        {
            _database = database;
            _emitEvent = emitEvent;
            _jsonOptions = jsonOptions;
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            // Events
            app.Get("/api/v1/events", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                EnumerationQuery query = new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => req.Query.GetValueOrDefault(key));
                string limitStr = req.Query.GetValueOrDefault("limit");
                if (!String.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out int limit)) query.PageSize = limit;
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<ArmadaEvent> result = ctx.IsAdmin
                    ? await _database.Events.EnumerateAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Events.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Events.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Events")
                .WithSummary("List events")
                .WithDescription("Returns system events, filterable by type, captainId, missionId, vesselId, or voyageId. Default limit is 50.")
                .WithParameter(OpenApiParameterMetadata.Query("type", "Filter by event type (e.g. mission.status_changed, captain.launched)", false))
                .WithParameter(OpenApiParameterMetadata.Query("captainId", "Filter by captain ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("missionId", "Filter by mission ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("vesselId", "Filter by vessel ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("voyageId", "Filter by voyage ID", false))
                .WithParameter(OpenApiParameterMetadata.Query("limit", "Maximum number of events to return (default: 50)", false, OpenApiSchemaMetadata.Integer()))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<ArmadaEvent>>("Paginated event list"))
                .WithSecurity("ApiKey"));

            app.Post<EnumerationQuery>("/api/v1/events/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                EnumerationQuery query = JsonSerializer.Deserialize<EnumerationQuery>(req.Http.Request.DataAsString, _jsonOptions) ?? new EnumerationQuery();
                query.ApplyQuerystringOverrides(key => req.Query.GetValueOrDefault(key));
                string limitStr = req.Query.GetValueOrDefault("limit");
                if (!String.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out int limit)) query.PageSize = limit;
                Stopwatch sw = Stopwatch.StartNew();
                EnumerationResult<ArmadaEvent> result = ctx.IsAdmin
                    ? await _database.Events.EnumerateAsync(query).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Events.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                        : await _database.Events.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return result;
            },
            api => api
                .WithTag("Events")
                .WithSummary("Enumerate events")
                .WithDescription("Paginated enumeration of events with optional filtering and sorting.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Enumeration query", false))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/events/token-usage", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }

                int days = 30;
                string daysValue = req.Query.GetValueOrDefault("days");
                if (!String.IsNullOrEmpty(daysValue) && int.TryParse(daysValue, out int parsedDays))
                    days = Math.Clamp(parsedDays, 1, 3650);

                DateTime toUtc = DateTime.UtcNow;
                DateTime fromUtc = toUtc.AddDays(-days);
                EnumerationQuery query = new EnumerationQuery
                {
                    PageSize = 1000,
                    CreatedAfter = fromUtc,
                    CreatedBefore = toUtc,
                    EventType = "mission.token_usage"
                };

                List<ArmadaEvent> events = new List<ArmadaEvent>();
                int totalPages = 1;
                for (int page = 1; page <= totalPages; page++)
                {
                    query.PageNumber = page;
                    EnumerationResult<ArmadaEvent> eventPage = ctx.IsAdmin
                        ? await _database.Events.EnumerateAsync(query).ConfigureAwait(false)
                        : ctx.IsTenantAdmin
                            ? await _database.Events.EnumerateAsync(ctx.TenantId!, query).ConfigureAwait(false)
                            : await _database.Events.EnumerateAsync(ctx.TenantId!, ctx.UserId!, query).ConfigureAwait(false);
                    events.AddRange(eventPage.Objects);
                    totalPages = eventPage.TotalPages;
                }

                return BuildTokenUsageSummary(events, fromUtc, toUtc);
            },
            api => api
                .WithTag("Events")
                .WithSummary("Summarize authoritative token usage")
                .WithDescription("Returns exact provider-reported token usage grouped by runtime and model. No tokenizer estimates are included.")
                .WithParameter(OpenApiParameterMetadata.Query("days", "Reporting window in days (1-3650, default 30)", false, OpenApiSchemaMetadata.Integer()))
                .WithResponse(200, OpenApiJson.For<TokenUsageSummary>("Authoritative token usage summary"))
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/events/{id}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                string id = req.Parameters["id"];
                ArmadaEvent? evt = ctx.IsAdmin
                    ? await _database.Events.ReadAsync(id).ConfigureAwait(false)
                    : ctx.IsTenantAdmin
                        ? await _database.Events.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                        : await _database.Events.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                if (evt == null) { req.Http.Response.StatusCode = 404; return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Event not found" }; }
                if (ctx.IsAdmin)
                    await _database.Events.DeleteAsync(id).ConfigureAwait(false);
                else if (ctx.IsTenantAdmin)
                    await _database.Events.DeleteAsync(ctx.TenantId!, id).ConfigureAwait(false);
                else
                    await _database.Events.DeleteAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                await _emitEvent("event.deleted", "Deleted event " + id,
                    "event", id, null, null, null, null).ConfigureAwait(false);
                req.Http.Response.StatusCode = 204;
                return null;
            },
            api => api
                .WithTag("Events")
                .WithSummary("Delete an event")
                .WithDescription("Permanently deletes an event by ID.")
                .WithParameter(OpenApiParameterMetadata.Path("id", "Event ID (evt_ prefix)"))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post<DeleteMultipleRequest>("/api/v1/events/delete/multiple", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                DeleteMultipleRequest? body = JsonSerializer.Deserialize<DeleteMultipleRequest>(req.Http.Request.DataAsString, _jsonOptions);
                if (body == null || body.Ids == null || body.Ids.Count == 0)
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Ids is required and must not be empty" };

                DeleteMultipleResult result = new DeleteMultipleResult();
                foreach (string id in body.Ids)
                {
                    if (String.IsNullOrEmpty(id))
                    {
                        result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                        continue;
                    }
                    ArmadaEvent? evt = ctx.IsAdmin
                        ? await _database.Events.ReadAsync(id).ConfigureAwait(false)
                        : ctx.IsTenantAdmin
                            ? await _database.Events.ReadAsync(ctx.TenantId!, id).ConfigureAwait(false)
                            : await _database.Events.ReadAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                    if (evt == null)
                    {
                        result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                        continue;
                    }
                    if (ctx.IsAdmin)
                        await _database.Events.DeleteAsync(id).ConfigureAwait(false);
                    else if (ctx.IsTenantAdmin)
                        await _database.Events.DeleteAsync(ctx.TenantId!, id).ConfigureAwait(false);
                    else
                        await _database.Events.DeleteAsync(ctx.TenantId!, ctx.UserId!, id).ConfigureAwait(false);
                    result.Deleted++;
                }

                await _emitEvent("event.batch_deleted", "Batch deleted " + result.Deleted + " events",
                    "event", null, null, null, null, null).ConfigureAwait(false);

                result.ResolveStatus();
                return (object)result;
            },
            api => api
                .WithTag("Events")
                .WithSummary("Batch delete multiple events")
                .WithDescription("Permanently deletes multiple events by ID. Returns a summary of deleted and skipped entries.")
                .WithRequestBody(OpenApiJson.BodyFor<DeleteMultipleRequest>("List of event IDs to delete"))
                .WithResponse(200, OpenApiJson.For<DeleteMultipleResult>("Delete result summary"))
                .WithSecurity("ApiKey"));
        }

        private TokenUsageSummary BuildTokenUsageSummary(
            List<ArmadaEvent> events,
            DateTime fromUtc,
            DateTime toUtc)
        {
            TokenUsageSummary summary = new TokenUsageSummary
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                CoverageNote = "Exact provider-reported usage only. OpenCode, Codex, Claude Code, Gemini, and Cursor emit authoritative counters; Mux is counted only when its provider event includes exact usage."
            };
            Dictionary<string, TokenUsageModelBreakdown> breakdowns = new Dictionary<string, TokenUsageModelBreakdown>(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> missions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            HashSet<string> reportedMissions = new HashSet<string>(StringComparer.Ordinal);

            foreach (ArmadaEvent evt in events)
            {
                if (String.IsNullOrWhiteSpace(evt.Payload))
                    continue;

                RuntimeTokenUsage? usage = null;
                try
                {
                    usage = JsonSerializer.Deserialize<RuntimeTokenUsage>(evt.Payload, _jsonOptions);
                }
                catch
                {
                    continue;
                }

                if (usage == null || String.IsNullOrWhiteSpace(usage.Runtime) || String.IsNullOrWhiteSpace(usage.Model))
                    continue;

                string key = usage.Runtime + "\n" + usage.Model;
                if (!breakdowns.TryGetValue(key, out TokenUsageModelBreakdown? breakdown))
                {
                    breakdown = new TokenUsageModelBreakdown
                    {
                        Runtime = usage.Runtime,
                        Model = usage.Model
                    };
                    breakdowns[key] = breakdown;
                    missions[key] = new HashSet<string>(StringComparer.Ordinal);
                }

                long sampleTotal = usage.ProviderTotalTokens ?? AddWithoutOverflow(usage.InputTokens, usage.OutputTokens);
                breakdown.SampleCount = AddWithoutOverflow(breakdown.SampleCount, 1);
                breakdown.InputTokens = AddWithoutOverflow(breakdown.InputTokens, usage.InputTokens);
                breakdown.OutputTokens = AddWithoutOverflow(breakdown.OutputTokens, usage.OutputTokens);
                breakdown.ReasoningTokens = AddWithoutOverflow(breakdown.ReasoningTokens, usage.ReasoningTokens);
                breakdown.CacheReadTokens = AddWithoutOverflow(breakdown.CacheReadTokens, usage.CacheReadTokens);
                breakdown.CacheWriteTokens = AddWithoutOverflow(breakdown.CacheWriteTokens, usage.CacheWriteTokens);
                breakdown.TotalTokens = AddWithoutOverflow(breakdown.TotalTokens, sampleTotal);
                summary.SampleCount = AddWithoutOverflow(summary.SampleCount, 1);

                if (!String.IsNullOrEmpty(evt.MissionId))
                {
                    missions[key].Add(evt.MissionId);
                    reportedMissions.Add(evt.MissionId);
                }
            }

            foreach (KeyValuePair<string, TokenUsageModelBreakdown> item in breakdowns)
            {
                item.Value.MissionCount = missions[item.Key].Count;
                summary.InputTokens = AddWithoutOverflow(summary.InputTokens, item.Value.InputTokens);
                summary.OutputTokens = AddWithoutOverflow(summary.OutputTokens, item.Value.OutputTokens);
                summary.ReasoningTokens = AddWithoutOverflow(summary.ReasoningTokens, item.Value.ReasoningTokens);
                summary.CacheReadTokens = AddWithoutOverflow(summary.CacheReadTokens, item.Value.CacheReadTokens);
                summary.CacheWriteTokens = AddWithoutOverflow(summary.CacheWriteTokens, item.Value.CacheWriteTokens);
                summary.TotalTokens = AddWithoutOverflow(summary.TotalTokens, item.Value.TotalTokens);
                summary.Models.Add(item.Value);
            }

            summary.ReportedMissionCount = reportedMissions.Count;
            summary.Models = summary.Models
                .OrderByDescending(item => item.TotalTokens)
                .ThenBy(item => item.Runtime, StringComparer.Ordinal)
                .ThenBy(item => item.Model, StringComparer.Ordinal)
                .ToList();
            return summary;
        }

        private static long AddWithoutOverflow(long left, long right)
        {
            if (right > 0 && left > Int64.MaxValue - right)
                return Int64.MaxValue;
            return left + right;
        }
    }
}
