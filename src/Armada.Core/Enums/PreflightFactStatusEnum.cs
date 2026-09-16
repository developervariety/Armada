namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of a preflight question the code can determine on its own. The fact is shown next to
    /// the operator's recorded answer so a recorded answer that disagrees with the repository is
    /// visible; the fact itself is informational and does not block dispatch.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PreflightFactStatusEnum
    {
        /// <summary>The repository confirms the condition the question asks about.</summary>
        Pass,

        /// <summary>The repository contradicts the condition the question asks about.</summary>
        Fail,

        /// <summary>The condition could not be determined from the repository.</summary>
        Unknown
    }
}
