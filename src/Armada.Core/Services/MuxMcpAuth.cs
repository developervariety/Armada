namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The credential Mux presents to an HTTP MCP server. Mux reads <see cref="Type"/> as none, bearer or
    /// apikey (ignoring case, hyphens and underscores), sends <see cref="BearerToken"/> as an Authorization
    /// bearer header, and sends <see cref="ApiKeyValue"/> in the <see cref="ApiKeyHeader"/> header. It
    /// expands a ${NAME} reference in either value from its environment, so a credential is referenced by
    /// variable name and never written into the file.
    /// </summary>
    public sealed class MuxMcpAuth
    {
        #region Public-Members

        /// <summary>
        /// Auth type that sends no credential.
        /// </summary>
        public const string NoneType = "none";

        /// <summary>
        /// Auth type that sends an Authorization bearer header.
        /// </summary>
        public const string BearerType = "bearer";

        /// <summary>
        /// Auth type that sends an API key in a named header.
        /// </summary>
        public const string ApiKeyType = "apikey";

        /// <summary>
        /// Header Mux uses for an API key when none is named.
        /// </summary>
        public const string DefaultApiKeyHeader = "X-API-Key";

        /// <summary>
        /// Auth type: none, bearer or apikey.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; } = null;

        /// <summary>
        /// Bearer token, or a ${NAME} reference to it.
        /// </summary>
        [JsonPropertyName("bearerToken")]
        public string? BearerToken { get; set; } = null;

        /// <summary>
        /// Header that carries the API key.
        /// </summary>
        [JsonPropertyName("apiKeyHeader")]
        public string? ApiKeyHeader { get; set; } = null;

        /// <summary>
        /// API key, or a ${NAME} reference to it.
        /// </summary>
        [JsonPropertyName("apiKeyValue")]
        public string? ApiKeyValue { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// A bearer credential.
        /// </summary>
        /// <param name="token">Token, or a ${NAME} reference to it.</param>
        /// <returns>The credential.</returns>
        public static MuxMcpAuth Bearer(string token)
        {
            if (String.IsNullOrWhiteSpace(token)) throw new ArgumentNullException(nameof(token));
            return new MuxMcpAuth { Type = BearerType, BearerToken = token };
        }

        /// <summary>
        /// An API key credential sent in a named header.
        /// </summary>
        /// <param name="header">Header name.</param>
        /// <param name="value">Key, or a ${NAME} reference to it.</param>
        /// <returns>The credential.</returns>
        public static MuxMcpAuth ApiKey(string header, string value)
        {
            if (String.IsNullOrWhiteSpace(header)) throw new ArgumentNullException(nameof(header));
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(value));
            return new MuxMcpAuth { Type = ApiKeyType, ApiKeyHeader = header, ApiKeyValue = value };
        }

        /// <summary>
        /// The request headers Mux sends for this credential. An unknown type, or a value that resolves to
        /// empty, sends nothing, as Mux does.
        /// </summary>
        /// <param name="resolveValue">Resolves a stored value (for example by expanding a ${NAME} reference).</param>
        /// <returns>Header names and values.</returns>
        public Dictionary<string, string> BuildHeaders(Func<string?, string> resolveValue)
        {
            if (resolveValue == null) throw new ArgumentNullException(nameof(resolveValue));
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string type = NormalizeType(Type);
            if (type == BearerType)
            {
                string token = resolveValue(BearerToken);
                if (!String.IsNullOrWhiteSpace(token)) headers["Authorization"] = "Bearer " + token;
            }
            else if (type == ApiKeyType)
            {
                string value = resolveValue(ApiKeyValue);
                if (!String.IsNullOrWhiteSpace(value))
                    headers[String.IsNullOrWhiteSpace(ApiKeyHeader) ? DefaultApiKeyHeader : ApiKeyHeader.Trim()] = value;
            }
            return headers;
        }

        #endregion

        #region Private-Methods

        private static string NormalizeType(string? type)
        {
            if (String.IsNullOrWhiteSpace(type)) return NoneType;
            return type.Replace("-", String.Empty).Replace("_", String.Empty).Trim().ToLowerInvariant();
        }

        #endregion
    }
}
