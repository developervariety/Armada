namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of a captain stop, deletion or restart request, shared by REST, MCP, WebSocket and the dashboard.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CaptainAdministrationOutcomeEnum
    {
        /// <summary>
        /// The operation completed.
        /// </summary>
        Completed,

        /// <summary>
        /// No captain with that identifier is visible to the caller. Nothing changed.
        /// </summary>
        NotFound,

        /// <summary>
        /// The captain is Working, Planning or Refining, or owns an Assigned or InProgress mission. Nothing changed.
        /// </summary>
        Busy,

        /// <summary>
        /// The operation failed before it wrote anything, so the captain record is unchanged.
        /// </summary>
        Failed,

        /// <summary>
        /// The captain is reserved by a planning or objective refinement session that could not be resolved for a
        /// coordinated stop. Nothing changed.
        /// </summary>
        Conflict
    }
}
