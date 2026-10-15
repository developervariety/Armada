namespace Armada.Server.Routes
{
    using System;
    using WatsonWebserver.Core;

    /// <summary>
    /// Reads path parameters from a request. Watson hands path parameters over still percent-encoded, so a persona
    /// named "Test Engineer" arrives as "Test%20Engineer". Name-valued parameters must be decoded before they are
    /// looked up. Decoding uses <see cref="Uri.UnescapeDataString(string)"/>, which leaves a literal '+' alone (path
    /// segments do not use '+' for space, unlike form-encoded query strings).
    /// </summary>
    public static class RouteParameters
    {
        /// <summary>
        /// Decode a percent-encoded path parameter value. A malformed escape sequence returns the value unchanged.
        /// </summary>
        /// <param name="value">Raw parameter value, or null.</param>
        /// <returns>The decoded value, or null when <paramref name="value"/> is null.</returns>
        public static string? Decode(string? value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return value;
            }
        }

        /// <summary>
        /// Read and decode a named path parameter of the request.
        /// </summary>
        /// <param name="req">The request.</param>
        /// <param name="name">Parameter name as written in the route template, for example "name".</param>
        /// <returns>The decoded value, or an empty string when the parameter is missing.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="req"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or empty.</exception>
        public static string Get(ApiRequest req, string name)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (String.IsNullOrEmpty(name)) throw new ArgumentException("Parameter name is required.", nameof(name));
            return Decode(req.Parameters[name]) ?? "";
        }
    }
}
