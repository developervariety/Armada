namespace Armada.Server.Routes
{
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;

    /// <summary>
    /// Admin-only REST routes for unattended lead status and primary selection.
    /// </summary>
    public class LeadControlRoutes
    {
        #region Private-Members

        private readonly LeadCycleCoordinator _Coordinator;
        private readonly JsonSerializerOptions _JsonOptions;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="coordinator">Shared lead-cycle coordinator.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        public LeadControlRoutes(LeadCycleCoordinator coordinator, JsonSerializerOptions jsonOptions)
        {
            _Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the admin-only lead-control routes.
        /// </summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Authentication callback.</param>
        /// <param name="authorization">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authorization)
        {
            app.Get("/api/v1/server/lead-control", async (ApiRequest request) =>
            {
                AuthContext? auth = await AuthorizeAsync(request, authenticate, authorization).ConfigureAwait(false);
                if (auth == null) return BuildAuthError(request);
                return await _Coordinator.GetStatusAsync().ConfigureAwait(false);
            },
            api => api
                .WithTag("Server")
                .WithSummary("Get unattended lead control status")
                .WithDescription("Returns the effective primary mode and shared lead-cycle lease.")
                .WithResponse(200, OpenApiJson.For<LeadCycleStatus>("Lead control status"))
                .WithSecurity("ApiKey"));

            app.Put<LeadModeUpdateRequest>("/api/v1/server/lead-control/mode", async (ApiRequest request) =>
            {
                AuthContext? auth = await AuthorizeAsync(request, authenticate, authorization).ConfigureAwait(false);
                if (auth == null) return BuildAuthError(request);
                LeadModeUpdateRequest? update = JsonSerializer.Deserialize<LeadModeUpdateRequest>(
                    request.Http.Request.DataAsString,
                    _JsonOptions);
                if (update == null || !update.Mode.HasValue)
                {
                    request.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse
                    {
                        Error = ApiResultEnum.BadRequest,
                        Message = "A lead mode is required."
                    };
                }

                string actor = auth.PrincipalDisplay
                    ?? auth.UserId
                    ?? auth.CredentialId
                    ?? "authenticated-admin";
                await _Coordinator.SetModeAsync(update.Mode.Value, actor).ConfigureAwait(false);
                return await _Coordinator.GetStatusAsync().ConfigureAwait(false);
            },
            api => api
                .WithTag("Server")
                .WithSummary("Set unattended lead mode")
                .WithDescription("Admin-only switch between LegacyPrimary, GrokPrimary, and Maintenance.")
                .WithRequestBody(OpenApiJson.BodyFor<LeadModeUpdateRequest>("Lead mode update", true))
                .WithResponse(200, OpenApiJson.For<LeadCycleStatus>("Updated lead control status"))
                .WithSecurity("ApiKey"));
        }

        #endregion

        #region Private-Methods

        private static async Task<AuthContext?> AuthorizeAsync(
            ApiRequest request,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authorization)
        {
            AuthContext auth = await authenticate(request.Http).ConfigureAwait(false);
            if (!authorization.IsAuthorized(
                auth,
                request.Http.Request.Method.ToString(),
                request.Http.Request.Url.RawWithoutQuery))
            {
                request.Http.Response.StatusCode = auth.IsAuthenticated ? 403 : 401;
                return null;
            }
            return auth;
        }

        private static ApiErrorResponse BuildAuthError(ApiRequest request)
        {
            return new ApiErrorResponse
            {
                Error = ApiResultEnum.BadRequest,
                Message = request.Http.Response.StatusCode == 401
                    ? "Authentication required"
                    : "You do not have permission to perform this action"
            };
        }

        #endregion
    }
}
