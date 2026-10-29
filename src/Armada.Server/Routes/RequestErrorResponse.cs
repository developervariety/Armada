namespace Armada.Server.Routes
{
    using WatsonWebserver.Core;
    using Armada.Core.Services;

    /// <summary>
    /// Maps a request-validation exception to HTTP 400. A collection-limit refusal and a launch-option
    /// refusal carry a machine-readable body in <see cref="ApiErrorResponse.Data"/>. Any other
    /// argument error keeps a message only.
    /// </summary>
    public static class RequestErrorResponse
    {
        #region Public-Methods

        /// <summary>
        /// Set status 400 and build the error response.
        /// </summary>
        /// <param name="req">The request whose status is set.</param>
        /// <param name="ex">The exception to map.</param>
        /// <returns>The error response.</returns>
        public static ApiErrorResponse BadRequest(ApiRequest req, Exception ex)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (ex == null) throw new ArgumentNullException(nameof(ex));
            req.Http.Response.StatusCode = 400;
            if (ex is CollectionLimitExceededException limit)
            {
                return new ApiErrorResponse
                {
                    Error = ApiResultEnum.BadRequest,
                    Message = limit.Message,
                    Data = CollectionLimitErrorDetail.FromException(limit)
                };
            }
            if (ex is EndpointLaunchOptionsException options)
            {
                return new ApiErrorResponse
                {
                    Error = ApiResultEnum.BadRequest,
                    Message = options.Message,
                    Data = new EndpointLaunchOptionsErrorDetail { Code = options.Code, Field = options.Field }
                };
            }
            return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
        }

        #endregion
    }
}
