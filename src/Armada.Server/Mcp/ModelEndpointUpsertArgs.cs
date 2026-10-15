namespace Armada.Server.Mcp
{
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Arguments for creating or updating a managed model endpoint via MCP.
    /// </summary>
    public class ModelEndpointUpsertArgs
    {
        #region Public-Members

        /// <summary>
        /// Endpoint identifier (mep_ prefix). Required for update, ignored for create.
        /// </summary>
        public string? EndpointId { get; set; } = null;

        /// <summary>
        /// Human-facing endpoint name.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// Whether the endpoint serves embeddings or inference.
        /// </summary>
        public ModelEndpointKindEnum? Kind { get; set; } = null;

        /// <summary>
        /// The provider/wire-format the endpoint speaks.
        /// </summary>
        public ModelProviderEnum? Provider { get; set; } = null;

        /// <summary>
        /// Base URL of the endpoint.
        /// </summary>
        public string? BaseUrl { get; set; } = null;

        /// <summary>
        /// The model identifier to request.
        /// </summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// API key/credential. Stored but never returned. On update, omit to keep the existing key.
        /// </summary>
        public string? ApiKey { get; set; } = null;

        /// <summary>
        /// Authentication type, or null to leave it unchanged (ProviderDefault on create).
        /// </summary>
        public ModelEndpointAuthTypeEnum? AuthType { get; set; } = null;

        /// <summary>
        /// Basic authentication user name, or null to leave it unchanged.
        /// </summary>
        public string? AuthUsername { get; set; } = null;

        /// <summary>
        /// Header that carries the secret for Header authentication, or null to leave it unchanged.
        /// </summary>
        public string? AuthHeaderName { get; set; } = null;

        /// <summary>
        /// Query parameter that carries the secret for QueryParameter authentication, or null to leave it unchanged.
        /// </summary>
        public string? AuthQueryParameter { get; set; } = null;

        /// <summary>
        /// Extra headers, or null to leave them unchanged. A supplied list replaces the stored one; a header without a
        /// value keeps the stored value of the header with the same name.
        /// </summary>
        public List<ModelEndpointHeader>? Headers { get; set; } = null;

        /// <summary>
        /// Optional embedding dimensionality hint.
        /// </summary>
        public int? Dimensionality { get; set; } = null;

        /// <summary>
        /// Per-request timeout in milliseconds.
        /// </summary>
        public int? TimeoutMs { get; set; } = null;

        /// <summary>
        /// Whether the endpoint is enabled for use and monitoring.
        /// </summary>
        public bool? Enabled { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelEndpointUpsertArgs()
        {
        }

        #endregion
    }
}
