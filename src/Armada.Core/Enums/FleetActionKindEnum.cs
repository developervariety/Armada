namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Supported fleet action kinds.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FleetActionKindEnum
    {
        /// <summary>Create mission work.</summary>
        Mission
    }
}
