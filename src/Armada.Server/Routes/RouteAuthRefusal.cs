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
