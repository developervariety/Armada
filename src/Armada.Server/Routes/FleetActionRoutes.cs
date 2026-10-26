namespace Armada.Server.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using WatsonWebserver;
    using WatsonWebserver.Core;

    /// <summary>Operator-owned saved mission actions and their durable runs.</summary>
    public sealed partial class FleetActionRoutes
    {
        private readonly FleetActionService _Service;
        private static readonly JsonSerializerOptions _BodyOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>Create the route group.</summary>
        /// <param name="service">Saved mission action service.</param>
        public FleetActionRoutes(FleetActionService service)
        {
            _Service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>Register definition and run endpoints.</summary>
        /// <param name="app">REST server.</param>
        /// <param name="authenticate">Request authentication.</param>
        /// <param name="authz">Route authorization.</param>
        public void Register(Webserver app, Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz)
        {
            RegisterDefinitions(app, authenticate, authz);
            RegisterRuns(app, authenticate, authz);
        }

        private static T ReadBody<T>(ApiRequest request) where T : class
        {
            return JsonSerializer.Deserialize<T>(request.Http.Request.DataAsString, _BodyOptions)
                ?? throw new ArgumentException("A request body is required.");
        }

        private static ApiErrorResponse Error(ApiRequest req, int status, ApiResultEnum code, string message)
        {
            req.Http.Response.StatusCode = status;
            return new ApiErrorResponse { Error = code, Message = message };
        }

        private static async Task<object> ExecuteAsync(ApiRequest req,
            Func<HttpContextBase, Task<AuthContext>> authenticate, IAuthorizationService authz,
            Func<AuthContext, Task<object>> operation)
        {
            AuthContext caller = await authenticate(req.Http).ConfigureAwait(false);
            if (!authz.IsAuthorized(caller, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery)
                || caller.MissionId != null || caller.AskThreadId != null || caller.AskTurnId != null)
                return RouteAuthRefusal.Refuse(req, caller);
            try
            {
                return await operation(caller).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return Error(req, 403, ApiResultEnum.Forbidden, "Current global administrator operator authority is required.");
            }
            catch (KeyNotFoundException)
            {
                return Error(req, 404, ApiResultEnum.NotFound, "The saved action or run was not found.");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
            {
                return Error(req, 400, ApiResultEnum.BadRequest, "The saved mission action request is invalid.");
            }
            catch (InvalidOperationException)
            {
                return Error(req, 409, ApiResultEnum.Conflict, "The request conflicts with the saved action or run state.");
            }
        }
    }
}
