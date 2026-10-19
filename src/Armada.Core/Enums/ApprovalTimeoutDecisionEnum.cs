namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Decision applied when an approval countdown expires.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ApprovalTimeoutDecisionEnum
    {
        /// <summary>Deny when nobody responds.</summary>
        Deny,
        /// <summary>Approve when nobody responds.</summary>
        Approve
    }
}
