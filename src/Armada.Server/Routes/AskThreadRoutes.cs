namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;

    /// <summary>REST routes for saved, owner-scoped Ask Armada threads.</summary>
    public sealed partial class AskThreadRoutes
    {
        private readonly AskThreadService _Threads;
        private static readonly JsonSerializerOptions _BodyOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Creates the route group.</summary>
        /// <param name="threads">Saved thread service.</param>
        public AskThreadRoutes(AskThreadService threads)
        {
            _Threads = threads ?? throw new ArgumentNullException(nameof(threads));
        }

        /// <summary>Registers authenticated saved-thread routes.</summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization rules.</param>
        public void Register(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            RegisterThreadRoutes(app, authenticate, authz);
            RegisterUnreadRoutes(app, authenticate, authz);
            RegisterTurnRoutes(app, authenticate, authz);
        }

        private void RegisterThreadRoutes(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Get("/api/v1/ask/threads", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                int page = ParseInt(req.Query.GetValueOrDefault("pageNumber"), 1);
                int size = ParseInt(req.Query.GetValueOrDefault("pageSize"), 100);
                bool archived = String.Equals(req.Query.GetValueOrDefault("includeArchived"), "true", StringComparison.OrdinalIgnoreCase);
                try { return await _Threads.EnumerateThreadsAsync(caller, page, size, archived).ConfigureAwait(false); }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("List saved Ask threads")
                .WithResponse(200, OpenApiJson.For<EnumerationResult<AskThread>>("Caller-owned threads"))
                .WithSecurity("ApiKey"));

            app.Post<AskThreadCreateRequest>("/api/v1/ask/threads", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                AskThreadCreateRequest body = Deserialize<AskThreadCreateRequest>(req);
                try { return await _Threads.CreateThreadAsync(caller, body).ConfigureAwait(false); }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Create a saved Ask thread")
                .WithRequestBody(OpenApiJson.BodyFor<AskThreadCreateRequest>("Optional title and captain", false))
                .WithResponse(200, OpenApiJson.For<AskThread>("Saved thread"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/ask/threads/{id}", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AskThread? thread = await _Threads.GetThreadAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    if (thread == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return thread;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Read saved Ask thread")
                .WithResponse(200, OpenApiJson.For<AskThread>("Thread metadata"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Put<AskThreadUpdateRequest>("/api/v1/ask/threads/{id}", async req =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                AskThreadUpdateRequest body = Deserialize<AskThreadUpdateRequest>(req);
                try
                {
                    AskThread? thread = await _Threads.UpdateThreadAsync(caller, req.Parameters["id"], body).ConfigureAwait(false);
                    if (thread == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return thread;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Update saved thread metadata")
                .WithRequestBody(OpenApiJson.BodyFor<AskThreadUpdateRequest>("Fields to update", true))
                .WithResponse(200, OpenApiJson.For<AskThread>("Updated thread"))
                .WithSecurity("ApiKey"));
        }

        private void RegisterUnreadRoutes(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            app.Post("/api/v1/ask/threads/{id}/read", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AskThread? thread = await _Threads.MarkThreadReadAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    if (thread == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return thread;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Mark a saved Ask thread read")
                .WithResponse(200, OpenApiJson.For<AskThread>("Thread with its unread count cleared"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/ask/threads/{id}/unread", async (ApiRequest req) =>
            {
                AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)) return RouteAuthRefusal.Refuse(req, caller);
                try
                {
                    AskThread? thread = await _Threads.MarkThreadUnreadAsync(caller, req.Parameters["id"]).ConfigureAwait(false);
                    if (thread == null) return Error(req, 404, ApiResultEnum.NotFound, "Ask thread not found.");
                    return thread;
                }
                catch (Exception ex) when (TryMapException(req, ex, out ApiErrorResponse error)) { return error; }
            }, api => api.WithTag("Ask Threads").WithSummary("Mark a saved Ask thread unread")
                .WithResponse(200, OpenApiJson.For<AskThread>("Thread with its unread count set to one"))
                .WithResponse(404, OpenApiResponseMetadata.NotFound())
                .WithSecurity("ApiKey"));
        }

        private static T Deserialize<T>(ApiRequest req) where T : class, new()
        {
            return JsonSerializer.Deserialize<T>(req.Http.Request.DataAsString, _BodyOptions) ?? new T();
        }

        private static int ParseInt(string? value, int fallback)
        {
            return Int32.TryParse(value, out int parsed) ? parsed : fallback;
        }

    }
}
