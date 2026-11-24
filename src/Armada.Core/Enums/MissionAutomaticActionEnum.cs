namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Automatic work that an opted-in mission requires separate permission to start.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MissionAutomaticActionEnum
    {
        /// <summary>Run the mission's mandatory definition-of-done verification.</summary>
        Verification,

        /// <summary>Prepare or admit work that depends on the mission.</summary>
        DependentAdmission
    }
}
