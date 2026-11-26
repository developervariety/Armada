namespace Armada.Core.Services
{
    using System.Text.Json.Serialization;

    /// <summary>The filesystem object supplied by a declared sibling artifact.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum SiblingArtifactKindEnum
    {
        Missing,
        Directory,
        File
    }
}
