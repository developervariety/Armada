namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using WatsonWebserver.Core;
    using Armada.Core.Enums;
    using Armada.Core.Services;

    public sealed partial class AskThreadRoutes
    {
        private static bool TryMapException(ApiRequest req, Exception ex, out ApiErrorResponse error)
        {
            if (ex is UnauthorizedAccessException)
            {
                error = Error(req, 403, ApiResultEnum.Forbidden, "Ask threads are not available to this caller.");
                return true;
            }
            if (ex is JsonException)
            {
                error = Error(req, 400, ApiResultEnum.BadRequest, "Request body is not valid JSON.");
                return true;
            }
            if (ex is CollectionLimitExceededException || ex is EndpointLaunchOptionsException)
            {
                error = RequestErrorResponse.BadRequest(req, ex);
                return true;
            }
            if (ex is ArgumentException)
            {
                error = Error(req, 400, ApiResultEnum.BadRequest, ex.Message);
                return true;
            }
            if (ex is InvalidOperationException)
            {
                error = Error(req, 409, ApiResultEnum.Conflict, ex.Message);
                return true;
            }
            error = null!;
            return false;
        }

        private static ApiErrorResponse Error(ApiRequest req, int status, ApiResultEnum code, string message)
        {
            req.Http.Response.StatusCode = status;
            return new ApiErrorResponse { Error = code, Message = message };
        }
    }
}
