namespace Armada.Server.Routes
{
    using System;
    using System.Threading.Tasks;
    using WatsonWebserver.Core;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one REST seam for the <c>{id}</c> path parameter of a GET-by-id route. An abbreviated id (prefix plus
    /// the eight-character creation-time part, such as <c>vyg_mabc1234</c>) resolves through
    /// <see cref="AbbreviatedIdResolver"/> against what the caller can see, the same rule the MCP tools apply.
    /// One visible match reads that record, so the route answers with its full id; no visible match leaves the
    /// id as given, so the route answers 404; two or more answer 409 with error <c>Conflict</c>, reason
    /// <c>ambiguous_id</c> and the candidate full ids.
    /// </summary>
    public static class RouteRecordId
    {
        #region Public-Methods

        /// <summary>
        /// Resolve the route's <c>id</c> path parameter for the authenticated caller.
        /// </summary>
        /// <param name="req">API request.</param>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="label">What the id names, such as "Voyage".</param>
        /// <returns>The id to read, or the refusal to return.</returns>
        public static async Task<RouteRecordIdResult> ResolveAsync(ApiRequest req, AuthContext caller, DatabaseDriver database, string label)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (database == null) throw new ArgumentNullException(nameof(database));

            string given = req.Parameters["id"] ?? String.Empty;
            AbbreviatedIdResolution resolved = await AbbreviatedIdResolver.ResolveForCallerAsync(database, caller, given).ConfigureAwait(false);
            if (!resolved.Ambiguous) return new RouteRecordIdResult(resolved.Id, null);

            req.Http.Response.StatusCode = 409;
            return new RouteRecordIdResult(given, new ApiErrorResponse
            {
                Error = ApiResultEnum.Conflict,
                Message = AbbreviatedIdResolver.DescribeAmbiguous(label, given),
                Data = new { Reason = AbbreviatedIdResolver.AmbiguousReason, Candidates = resolved.Candidates }
            });
        }

        #endregion
    }
}
