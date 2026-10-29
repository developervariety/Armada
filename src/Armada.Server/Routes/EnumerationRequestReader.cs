namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using WatsonWebserver.Core;
    using Armada.Core.Models;

    /// <summary>
    /// Reads the paging and filter query of a <c>POST .../enumerate</c> route: the JSON body (optional) with the
    /// querystring values applied on top, the way the other enumerate routes read it.
    /// </summary>
    internal static class EnumerationRequestReader
    {
        #region Public-Methods

        /// <summary>
        /// Read the query.
        /// </summary>
        /// <param name="req">API request.</param>
        /// <param name="options">Serializer options for the body.</param>
        /// <param name="query">The query, which is page 1 of 10 when the request sends nothing.</param>
        /// <param name="error">Why the body could not be read, or null.</param>
        /// <returns>True when the query was read; false when the body is not a valid enumeration query.</returns>
        /// <exception cref="ArgumentNullException">Thrown when req or options is null.</exception>
        public static bool TryRead(ApiRequest req, JsonSerializerOptions options, out EnumerationQuery query, out string? error)
        {
            return TryRead<EnumerationQuery>(req, options, out query, out error);
        }

        /// <summary>
        /// Read a query that extends <see cref="EnumerationQuery"/> with its own filters. The body carries those
        /// filters; the querystring then overrides the standard fields only.
        /// </summary>
        /// <typeparam name="TQuery">Query type to read.</typeparam>
        /// <param name="req">API request.</param>
        /// <param name="options">Serializer options for the body.</param>
        /// <param name="query">The query, which is page 1 of 10 when the request sends nothing.</param>
        /// <param name="error">Why the body could not be read, or null.</param>
        /// <returns>True when the query was read; false when the body is not a valid query.</returns>
        /// <exception cref="ArgumentNullException">Thrown when req or options is null.</exception>
        public static bool TryRead<TQuery>(ApiRequest req, JsonSerializerOptions options, out TQuery query, out string? error)
            where TQuery : EnumerationQuery, new()
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (options == null) throw new ArgumentNullException(nameof(options));

            error = null;
            string body = req.Http.Request.DataAsString;
            try
            {
                query = String.IsNullOrWhiteSpace(body)
                    ? new TQuery()
                    : JsonSerializer.Deserialize<TQuery>(body, options) ?? new TQuery();
            }
            catch (JsonException ex)
            {
                query = new TQuery();
                error = "The request body is not a valid enumeration query: " + ex.Message;
                return false;
            }

            query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
            return true;
        }

        #endregion

        #region Private-Methods

        private static bool TryRead<TQuery>(ApiRequest req, JsonSerializerOptions options, out TQuery query, out string? error, Func<TQuery> create)
            where TQuery : EnumerationQuery
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (options == null) throw new ArgumentNullException(nameof(options));

            error = null;
            string body = req.Http.Request.DataAsString;
            try
            {
                query = String.IsNullOrWhiteSpace(body)
                    ? create()
                    : JsonSerializer.Deserialize<TQuery>(body, options) ?? create();
            }
            catch (JsonException ex)
            {
                query = create();
                error = "The request body is not a valid enumeration query: " + ex.Message;
                return false;
            }

            query.ApplyQuerystringOverrides(key => QueryValueReader.Read(req, key));
            return true;
        }

        #endregion
    }
}
