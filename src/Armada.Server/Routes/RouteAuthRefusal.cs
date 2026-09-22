namespace Armada.Server.Routes
{
    using System;
    using WatsonWebserver.Core;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the response a REST route returns when the caller fails authentication or authorization.
    /// <para>
    /// An unauthenticated caller receives HTTP 401 with error <see cref="ApiResultEnum.NotAuthorized"/>;
    /// an authenticated caller without permission receives HTTP 403 with error
    /// <see cref="ApiResultEnum.Forbidden"/>. The body error always names the same outcome as the status.
    /// </para>
    /// </summary>
    public static class RouteAuthRefusal
    {
        #region Public-Members

        /// <summary>
        /// Message returned to a caller that is not authenticated.
        /// </summary>
        public const string AuthenticationRequiredMessage = "Authentication required";

        /// <summary>
        /// Message returned to an authenticated caller that lacks permission.
        /// </summary>
        public const string PermissionDeniedMessage = "You do not have permission to perform this action";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Set the refusal status on the response and build the matching error body.
        /// </summary>
        /// <param name="req">API request whose response status is set.</param>
        /// <param name="ctx">Authentication context of the caller; null counts as unauthenticated.</param>
        /// <returns>The error body for the refusal.</returns>
        public static ApiErrorResponse Refuse(ApiRequest req, AuthContext? ctx)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            bool authenticated = ctx != null && ctx.IsAuthenticated;
            req.Http.Response.StatusCode = authenticated ? 403 : 401;
            return Build(authenticated);
        }

        /// <summary>
        /// Set the refusal status on the response and build the matching error body, naming what an authenticated
        /// caller lacks with <paramref name="permissionDeniedMessage"/>.
        /// </summary>
        /// <param name="req">API request whose response status is set.</param>
        /// <param name="ctx">Authentication context of the caller; null counts as unauthenticated.</param>
        /// <param name="permissionDeniedMessage">Message returned to an authenticated caller that lacks permission.</param>
        /// <returns>The error body for the refusal.</returns>
        public static ApiErrorResponse Refuse(ApiRequest req, AuthContext? ctx, string permissionDeniedMessage)
        {
            ApiErrorResponse response = Refuse(req, ctx);
            if (response.Error == ApiResultEnum.Forbidden && !String.IsNullOrEmpty(permissionDeniedMessage))
                response.Message = permissionDeniedMessage;
            return response;
        }

        /// <summary>
        /// Refuse an authenticated caller that lacks permission: set HTTP 403 and build a
        /// <see cref="ApiResultEnum.Forbidden"/> body carrying <paramref name="message"/>.
        /// </summary>
        /// <param name="req">API request whose response status is set.</param>
        /// <param name="message">Message naming what the caller lacks.</param>
        /// <returns>The error body for the refusal.</returns>
        public static ApiErrorResponse Forbid(ApiRequest req, string message)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            req.Http.Response.StatusCode = 403;
            ApiErrorResponse response = Build(true);
            if (!String.IsNullOrEmpty(message)) response.Message = message;
            return response;
        }

        /// <summary>
        /// Build the error body for a refusal whose status an authorization helper has already set on the response:
        /// 401 names <see cref="ApiResultEnum.NotAuthorized"/>, any other status names <see cref="ApiResultEnum.Forbidden"/>.
        /// </summary>
        /// <param name="req">API request whose response status is already set.</param>
        /// <returns>The error body for the refusal.</returns>
        public static ApiErrorResponse FromStatus(ApiRequest req)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            return Build(req.Http.Response.StatusCode != 401);
        }

        /// <summary>
        /// Build the error body for a refusal without touching a response.
        /// </summary>
        /// <param name="authenticated">True when the caller is authenticated but lacks permission.</param>
        /// <returns>The error body for the refusal.</returns>
        public static ApiErrorResponse Build(bool authenticated)
        {
            return new ApiErrorResponse
            {
                Error = authenticated ? ApiResultEnum.Forbidden : ApiResultEnum.NotAuthorized,
                Message = authenticated ? PermissionDeniedMessage : AuthenticationRequiredMessage
            };
        }

        #endregion
    }
}
