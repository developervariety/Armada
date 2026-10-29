namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>State of one native tool permission request raised by an attended Ask turn.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskToolPermissionStateEnum
    {
        /// <summary>Waiting for the thread owner's decision.</summary>
        Pending,
        /// <summary>The tool call was allowed.</summary>
        Allowed,
        /// <summary>The tool call was denied.</summary>
        Denied,
        /// <summary>No decision arrived before the request expired, so the call was denied.</summary>
        Expired
    }
}
