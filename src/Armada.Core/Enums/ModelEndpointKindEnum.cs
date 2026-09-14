namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The kind of model an endpoint serves. Runtime integrations use this value to select an embedding or
    /// completion request; explicit endpoint validation checks the provider response for this kind.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelEndpointKindEnum
    {
        /// <summary>
        /// An embedding model endpoint: text in, a vector out.
        /// </summary>
        [EnumMember(Value = "Embedding")]
        Embedding,

        /// <summary>
        /// An inference (chat/completion) model endpoint.
        /// </summary>
        [EnumMember(Value = "Inference")]
        Inference
    }
}
