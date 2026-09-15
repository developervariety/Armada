namespace Armada.Server.Routes
{
    using System;
    using WatsonWebserver.Core;

    /// <summary>
    /// Reads query-string values for REST routes.
    /// <para>
    /// The web server hands routes the raw query text: neither keys nor values are percent-decoded,
    /// so a browser client that encodes a timestamp's colons (<c>%3A</c>) would otherwise send a
    /// value no parser accepts, and the route would silently fall back to its default. Every route
    /// that reads a query value goes through this reader, so each value is decoded exactly once.
    /// </para>
    /// <para>
    /// A <c>+</c> is kept as a literal plus, not read as a space. That keeps an unencoded UTC offset
    /// such as <c>+00:00</c> valid; a client that means a space sends <c>%20</c>.
    /// </para>
    /// </summary>
    public static class QueryValueReader
    {
        #region Public-Methods

        /// <summary>
        /// Read one query-string value and percent-decode it.
        /// </summary>
        /// <param name="req">API request.</param>
        /// <param name="key">Query key, matched as the client sent it.</param>
        /// <returns>The decoded value, or null when the key is absent.</returns>
        public static string? Read(ApiRequest req, string key)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (String.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            return Decode(req.Query.GetValueOrDefault(key));
        }

        /// <summary>
        /// Percent-decode one raw query-string value. A malformed escape is kept as sent.
        /// </summary>
        /// <param name="raw">Raw value as it appeared in the query string.</param>
        /// <returns>The decoded value, or null when the input is null.</returns>
        public static string? Decode(string? raw)
        {
            if (raw == null) return null;
            if (raw.IndexOf('%') < 0) return raw;
            return Uri.UnescapeDataString(raw);
        }

        #endregion
    }
}
