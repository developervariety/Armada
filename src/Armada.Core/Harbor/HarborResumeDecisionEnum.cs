namespace Armada.Core.Harbor
{
    using System.Text.Json.Serialization;

    /// <summary>Server decision for a resume proof.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HarborResumeDecisionEnum
    {
        /// <summary>Job identity is verified; replay output from the persisted sequence.</summary>
        Replay,

        /// <summary>Server requested a stop, but has not marked the job terminal.</summary>
        Stop,

        /// <summary>Proof is invalid or cannot be adopted.</summary>
        Reject
    }
}
