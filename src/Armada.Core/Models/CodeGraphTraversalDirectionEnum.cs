namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Direction options for graph traversal.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CodeGraphTraversalDirectionEnum
    {
        /// <summary>
        /// Traverse only incoming call edges (callers).
        /// </summary>
        Callers,

        /// <summary>
        /// Traverse only outgoing call edges (callees).
        /// </summary>
        Callees,

        /// <summary>
        /// Traverse both incoming and outgoing call edges.
        /// </summary>
        Both
    }
}
