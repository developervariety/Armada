namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// REST API route for the operator "needs you" inbox.
    /// </summary>
    public class InboxRoutes
    {
        private readonly InboxService _inbox;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public InboxRoutes(InboxService inbox, JsonSerializerOptions jsonOptions)
        {
            _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
            _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Post("/api/v1/inbox/enumerate", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                if (!EnumerationRequestReader.TryRead(req, _jsonOptions, out EnumerationQuery query, out string? queryError))
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = queryError };
                }

                List<InboxItem> items = await _inbox.GetInboxAsync(ctx).ConfigureAwait(false);
                return EnumerationResult<InboxItem>.Page(query, items);
            },
            api => api
                .WithTag("Inbox")
                .WithSummary("Enumerate the needs-you inbox")
                .WithDescription("Returns one page of the needs-you inbox, most urgent first; TotalRecords is the whole inbox count. Paging comes from the JSON body (EnumerationQuery) or the querystring (pageNumber, pageSize 1 to 1000, default 10); order and filters do not apply.")
                .WithRequestBody(OpenApiJson.BodyFor<EnumerationQuery>("Paging", false))
                .WithResponse(200, OpenApiJson.For<EnumerationResult<InboxItem>>("One page of inbox items"))
                .WithResponse(400, OpenApiResponseMetadata.BadRequest())
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/inbox", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                List<InboxItem> items = await _inbox.GetInboxAsync(ctx).ConfigureAwait(false);
                return items;
            },
            api => api
                .WithTag("Inbox")
                .WithSummary("Get the needs-you inbox")
                .WithDescription("Returns a consolidated, most-urgent-first list of items awaiting operator attention (reviews, failed landings, failed missions, stalled captains), plus the caller's own pending Ask action proposals.")
                .WithResponse(200, OpenApiJson.For<List<InboxItem>>("Inbox items"))
                .WithSecurity("ApiKey"));
        }
    }
}
