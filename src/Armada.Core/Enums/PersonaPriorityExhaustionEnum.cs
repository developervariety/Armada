namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Controls routing after an ordered captain hierarchy is exhausted.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PersonaPriorityExhaustionEnum
    {
        /// <summary>Keep the mission pending when no priority has an available match.</summary>
        Wait,
        /// <summary>Allow legacy persona and tier routing after all priorities are exhausted.</summary>
        UseEligibleCaptain
    }
}
