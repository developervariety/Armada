namespace Armada.Server
{
    using System.Collections.Generic;
    using WatsonWebserver.Core.OpenApi;

    /// <summary>
    /// Fluent helpers on <see cref="OpenApiRouteMetadata"/> preserving the call sites
    /// previously provided by SwiftStack's OpenAPI extensions.
    /// </summary>
    public static class OpenApiRouteMetadataExtensions
    {
        /// <summary>
        /// Set the operation summary.
        /// </summary>
        /// <param name="metadata">Route metadata.</param>
        /// <param name="summary">Short summary string.</param>
        /// <returns>The same metadata instance for chaining.</returns>
        public static OpenApiRouteMetadata WithSummary(this OpenApiRouteMetadata metadata, string summary)
        {
            if (metadata == null) throw new System.ArgumentNullException(nameof(metadata));
            metadata.Summary = summary;
            return metadata;
        }

        /// <summary>
        /// Add a named security scheme requirement to the operation.
        /// </summary>
        /// <param name="metadata">Route metadata.</param>
        /// <param name="schemeName">Security scheme name (e.g., "ApiKey").</param>
        /// <returns>The same metadata instance for chaining.</returns>
        public static OpenApiRouteMetadata WithSecurity(this OpenApiRouteMetadata metadata, string schemeName)
        {
            if (metadata == null) throw new System.ArgumentNullException(nameof(metadata));
            if (metadata.Security == null) metadata.Security = new List<string>();
            metadata.Security.Add(schemeName);
            return metadata;
        }
    }

    /// <summary>
    /// Typed response factories for <see cref="OpenApiResponseMetadata"/>. Preserves the
    /// <c>OpenApiResponseMetadata.Json&lt;T&gt;(description)</c> shape that SwiftStack exposed.
    /// </summary>
    public static class OpenApiJson
    {
        /// <summary>
        /// Create a JSON response descriptor for the specified payload type.
        /// </summary>
        /// <typeparam name="T">Payload type.</typeparam>
        /// <param name="description">Response description.</param>
        /// <returns>An OpenApiResponseMetadata for the application/json content type.</returns>
        public static OpenApiResponseMetadata For<T>(string description)
        {
            OpenApiSchemaMetadata schema = new OpenApiSchemaMetadata
            {
                Type = "object",
                Description = typeof(T).Name
            };
            return OpenApiResponseMetadata.Json(description, schema);
        }

        /// <summary>
        /// Create a JSON request body descriptor for the specified payload type.
        /// </summary>
        /// <typeparam name="T">Payload type.</typeparam>
        /// <param name="description">Body description.</param>
        /// <param name="required">Whether the body is required.</param>
        /// <returns>An OpenApiRequestBodyMetadata for the application/json content type.</returns>
        public static OpenApiRequestBodyMetadata BodyFor<T>(string description, bool required = true)
        {
            OpenApiSchemaMetadata schema = new OpenApiSchemaMetadata
            {
                Type = "object",
                Description = typeof(T).Name
            };
            return OpenApiRequestBodyMetadata.Json(schema, description, required);
        }
    }
}
