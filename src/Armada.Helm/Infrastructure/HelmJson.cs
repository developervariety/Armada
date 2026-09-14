namespace Armada.Helm.Infrastructure
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The one JSON contract Helm uses for Admiral request and response bodies.
    /// The Admiral writes enum values by name, and several request enums carry no
    /// converter attribute, so the converter belongs to the options rather than the types.
    /// </summary>
    internal static class HelmJson
    {
        #region Public-Members

        /// <summary>
        /// Serializer options for Admiral REST bodies and indented command output.
        /// </summary>
        internal static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        #endregion
    }
}
