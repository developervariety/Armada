namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a model endpoint authenticates. The secret is the endpoint's write-only credential (<c>apiKey</c> in JSON);
    /// extra headers configured on the endpoint are sent with every type.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelEndpointAuthTypeEnum
    {
        /// <summary>
        /// The provider's own scheme: <c>Authorization: Bearer</c> for Ollama, OpenAI, OpenAI-compatible, and Voyage AI;
        /// <c>x-api-key</c> for Anthropic; <c>x-goog-api-key</c> for Gemini; <c>api-key</c> for Azure OpenAI; the
        /// service-account JSON for Vertex AI; the secret access key for AWS Bedrock. The only type Vertex AI and
        /// Bedrock accept, because they sign requests.
        /// </summary>
        [EnumMember(Value = "ProviderDefault")]
        ProviderDefault,

        /// <summary>
        /// No credential is sent (extra headers still are).
        /// </summary>
        [EnumMember(Value = "None")]
        None,

        /// <summary>
        /// <c>Authorization: Bearer &lt;secret&gt;</c>.
        /// </summary>
        [EnumMember(Value = "Bearer")]
        Bearer,

        /// <summary>
        /// <c>Authorization: Basic base64(username:secret)</c>, with the username in <c>AuthUsername</c>.
        /// </summary>
        [EnumMember(Value = "Basic")]
        Basic,

        /// <summary>
        /// A custom header, <c>&lt;AuthHeaderName&gt;: &lt;secret&gt;</c> (for example <c>X-API-Key</c>).
        /// </summary>
        [EnumMember(Value = "Header")]
        Header,

        /// <summary>
        /// A query string parameter, <c>?&lt;AuthQueryParameter&gt;=&lt;secret&gt;</c>, appended to every request URL.
        /// </summary>
        [EnumMember(Value = "QueryParameter")]
        QueryParameter
    }
}
