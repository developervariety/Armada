namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Administrator routes for durable Harbor runner enrollment and revocation. Registered only when Harbor is
    /// enabled.
    /// </summary>
    public sealed class HarborRunnerEnrollmentRoutes
    {
        private readonly HarborRunnerEnrollmentService _Enrollments;
        private readonly JsonSerializerOptions _JsonOptions;

        /// <summary>Instantiate the Harbor runner enrollment routes.</summary>
        /// <param name="enrollments">Durable enrollment service.</param>
        /// <param name="jsonOptions">Application JSON options.</param>
        public HarborRunnerEnrollmentRoutes(HarborRunnerEnrollmentService enrollments, JsonSerializerOptions jsonOptions)
        {
            _Enrollments = enrollments ?? throw new ArgumentNullException(nameof(enrollments));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>Register administrator enrollment and revocation routes.</summary>
        /// <param name="app">Web server.</param>
        /// <param name="authenticate">Verified request authentication.</param>
        /// <param name="authz">Common authorization service.</param>
        public void Register(
            Webserver app,
            Func<HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (authenticate == null) throw new ArgumentNullException(nameof(authenticate));
            if (authz == null) throw new ArgumentNullException(nameof(authz));

            app.Post("/api/v1/harbor-runners/enrollments", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "POST", "/api/v1/harbor-runners/enrollments")) return Denied(req, context);
                HarborRunnerEnrollmentRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<HarborRunnerEnrollmentRequest>(req.Http.Request.DataAsString ?? String.Empty, _JsonOptions);
                }
                catch (JsonException exception)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Invalid Harbor enrollment request: " + exception.Message };
                }
                if (request == null || String.IsNullOrWhiteSpace(request.RunnerId) || String.IsNullOrWhiteSpace(request.CredentialId))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "runnerId and credentialId are required" };
                }
                try
                {
                    return (object)await _Enrollments.CreateForCredentialAsync(request.RunnerId, request.CredentialId, context, req.Http.Token).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException exception)
                {
                    req.Http.Response.StatusCode = 403;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = exception.Message };
                }
                catch (InvalidOperationException exception)
                {
                    req.Http.Response.StatusCode = 409;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = exception.Message };
                }
                catch (ArgumentException exception)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = exception.Message };
                }
            });

            app.Post("/api/v1/harbor-runners/enrollments/{runnerId}/revoke", async (ApiRequest req) =>
            {
                AuthContext context = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(context, "POST", "/api/v1/harbor-runners/enrollments/revoke")) return Denied(req, context);
                try
                {
                    bool revoked = await _Enrollments.RevokeAsync(req.Parameters["runnerId"] ?? String.Empty, context, req.Http.Token).ConfigureAwait(false);
                    if (!revoked)
                    {
                        req.Http.Response.StatusCode = 404;
                        return (object)new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "Harbor runner enrollment not found or already revoked" };
                    }
                    return (object)new HarborRunnerEnrollmentRevocationResponse { Revoked = true };
                }
                catch (UnauthorizedAccessException exception)
                {
                    req.Http.Response.StatusCode = 403;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = exception.Message };
                }
                catch (ArgumentException exception)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = exception.Message };
                }
            });
        }

        private static object Denied(ApiRequest request, AuthContext context)
        {
            bool authenticated = context != null && context.IsAuthenticated;
            request.Http.Response.StatusCode = authenticated ? 403 : 401;
            return new ApiErrorResponse
            {
                Error = ApiResultEnum.BadRequest,
                Message = authenticated ? "Administrator permission required" : "Authentication required"
            };
        }
    }

    /// <summary>Typed result returned after a Harbor runner enrollment is revoked.</summary>
    public sealed class HarborRunnerEnrollmentRevocationResponse
    {
        /// <summary>Whether an active enrollment was revoked.</summary>
        public bool Revoked { get; set; }
    }
}
