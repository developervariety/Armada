namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Controls whether an unavailable first captain class permits lower priorities.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PersonaPriorityAvailabilityEnum
    {
        /// <summary>Try each priority in order using currently available captains.</summary>
        FallThrough,
        /// <summary>Wait until the first priority has an available eligible captain.</summary>
        WaitForFirstPriority
    }
}
