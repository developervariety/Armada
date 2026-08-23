namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API routes for the shared coordination board (chatroom).
    /// </summary>
    public class CoordinationRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly CoordinationService _coordination;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationRoutes(
            DatabaseDriver database,
            CoordinationService coordination,
            JsonSerializerOptions jsonOptions)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
            _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/coordination/rooms", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                List<CoordinationRoom> rooms = await _coordination.EnumerateRoomsAsync().ConfigureAwait(false);
                return rooms;
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("List coordination rooms")
                .WithDescription("Returns coordination rooms ordered by most recent activity. Creates the default fleet room when none exists.")
                .WithResponse(200, OpenApiJson.For<List<CoordinationRoom>>("Coordination rooms"))
                .WithSecurity("ApiKey"));

            app.Post<CoordinationRoomCreateRequest>("/api/v1/coordination/rooms", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CoordinationRoomCreateRequest request = JsonSerializer.Deserialize<CoordinationRoomCreateRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CoordinationRoomCreateRequest();

                if (String.IsNullOrWhiteSpace(request.Key))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Key is required." };
                }

                CoordinationRoom? existing = await _database.CoordinationRooms.ReadByKeyAsync(request.Key!).ConfigureAwait(false);
                if (existing != null)
                {
                    req.Http.Response.StatusCode = 200;
                    return existing;
                }

                CoordinationRoom room = new CoordinationRoom
                {
                    TenantId = ctx.TenantId,
                    UserId = ctx.UserId,
                    Key = request.Key!,
                    Name = String.IsNullOrWhiteSpace(request.Name) ? request.Key! : request.Name!,
                    Description = request.Description
                };
                room = await _database.CoordinationRooms.CreateAsync(room).ConfigureAwait(false);
                req.Http.Response.StatusCode = 201;
                return room;
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("Create a coordination room")
                .WithDescription("Creates a coordination room, or returns the existing room with the same key.")
                .WithResponse(201, OpenApiJson.For<CoordinationRoom>("Coordination room"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/coordination/rooms/{key}/messages", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                string key = req.Parameters["key"];
                int limit = ParseIntQuery(req, "limit", 200);
                DateTime? afterUtc = ParseDateTimeQuery(req, "afterUtc");

                try
                {
                    List<CoordinationMessage> messages = await _coordination.ReadMessagesAsync(key, afterUtc, limit).ConfigureAwait(false);
                    return messages;
                }
                catch (NotSupportedException ex)
                {
                    req.Http.Response.StatusCode = 501;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("Read coordination room messages")
                .WithDescription("Returns messages from a coordination room oldest first. Use afterUtc for incremental reads.")
                .WithResponse(200, OpenApiJson.For<List<CoordinationMessage>>("Coordination messages"))
                .WithSecurity("ApiKey"));

            app.Post<CoordinationMessagePostRequest>("/api/v1/coordination/rooms/{key}/messages", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                string key = req.Parameters["key"];
                CoordinationMessagePostRequest request = JsonSerializer.Deserialize<CoordinationMessagePostRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CoordinationMessagePostRequest();

                if (String.IsNullOrWhiteSpace(request.Content))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Content is required." };
                }
                if (String.IsNullOrWhiteSpace(request.AuthorName))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "AuthorName is required." };
                }

                CoordinationAuthorTypeEnum authorType = request.AuthorType ?? CoordinationAuthorTypeEnum.Operator;

                try
                {
                    CoordinationMessage message = await _coordination.PostMessageAsync(
                        key,
                        authorType,
                        request.AuthorId,
                        request.AuthorName!,
                        request.Content!,
                        request.VoyageId,
                        request.MissionId,
                        request.VesselId,
                        request.IncidentId,
                        ctx.TenantId).ConfigureAwait(false);
                    req.Http.Response.StatusCode = 201;
                    return message;
                }
                catch (NotSupportedException ex)
                {
                    req.Http.Response.StatusCode = 501;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("Post a coordination message")
                .WithDescription("Posts a note to a coordination room on behalf of a participant.")
                .WithResponse(201, OpenApiJson.For<CoordinationMessage>("Coordination message"))
                .WithSecurity("ApiKey"));

            app.Post<CoordinationPresenceRequest>("/api/v1/coordination/rooms/{key}/presence", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                string key = req.Parameters["key"];
                CoordinationPresenceRequest request = JsonSerializer.Deserialize<CoordinationPresenceRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? new CoordinationPresenceRequest();

                if (String.IsNullOrWhiteSpace(request.ParticipantKey) || String.IsNullOrWhiteSpace(request.DisplayName))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "ParticipantKey and DisplayName are required." };
                }

                try
                {
                    CoordinationParticipant participant = await _coordination.HeartbeatAsync(key, request.ParticipantKey!, request.DisplayName!, ctx.TenantId).ConfigureAwait(false);
                    return participant;
                }
                catch (NotSupportedException ex)
                {
                    req.Http.Response.StatusCode = 501;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("Send a presence heartbeat")
                .WithDescription("Refreshes a participant's presence in a coordination room.")
                .WithResponse(200, OpenApiJson.For<CoordinationParticipant>("Coordination participant"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/coordination/claims", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                CoordinationClaimSubjectEnum? subjectType = null;
                string? rawSubjectType = req.Query.GetValueOrDefault("subjectType");
                if (!String.IsNullOrEmpty(rawSubjectType) &&
                    Enum.TryParse(rawSubjectType!, true, out CoordinationClaimSubjectEnum parsed))
                {
                    subjectType = parsed;
                }
                string? subjectId = req.Query.GetValueOrDefault("subjectId");

                List<CoordinationClaim> claims = await _coordination.EnumerateActiveClaimsAsync(subjectType, subjectId).ConfigureAwait(false);
                return claims;
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("List active coordination claims")
                .WithDescription("Returns unexpired work reservations, optionally narrowed by subjectType and subjectId.")
                .WithResponse(200, OpenApiJson.For<List<CoordinationClaim>>("Coordination claims"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/coordination/rooms/{key}/participants", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse
                    {
                        Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest,
                        Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required"
                    };
                }

                string key = req.Parameters["key"];
                int activeWithinMinutes = ParseIntQuery(req, "activeWithinMinutes", 15);

                try
                {
                    List<CoordinationParticipant> participants = await _coordination.EnumerateParticipantsAsync(key, activeWithinMinutes).ConfigureAwait(false);
                    return participants;
                }
                catch (NotSupportedException ex)
                {
                    req.Http.Response.StatusCode = 501;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
            },
            api => api
                .WithTag("Coordination")
                .WithSummary("List active participants")
                .WithDescription("Returns participants seen in the room within the given window, most recently active first.")
                .WithResponse(200, OpenApiJson.For<List<CoordinationParticipant>>("Coordination participants"))
                .WithSecurity("ApiKey"));
        }

        private static int ParseIntQuery(ApiRequest req, string name, int defaultValue)
        {
            string? raw = req.Query.GetValueOrDefault(name);
            if (String.IsNullOrEmpty(raw)) return defaultValue;
            if (!Int32.TryParse(raw, out int value)) return defaultValue;
            return value;
        }

        private static DateTime? ParseDateTimeQuery(ApiRequest req, string name)
        {
            string? raw = req.Query.GetValueOrDefault(name);
            if (String.IsNullOrEmpty(raw)) return null;
            if (!DateTime.TryParse(raw, out DateTime value)) return null;
            return value.ToUniversalTime();
        }
    }
}
