namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of a create, update or delete of a configuration record through its shared service.
    /// Every surface (REST, MCP, WebSocket) maps the same outcome to its own response shape.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RecordWriteOutcomeEnum
    {
        /// <summary>
        /// The write was applied.
        /// </summary>
        [EnumMember(Value = "Succeeded")]
        Succeeded,

        /// <summary>
        /// The request was refused because a field is missing or invalid. Nothing was written.
        /// </summary>
        [EnumMember(Value = "Invalid")]
        Invalid,

        /// <summary>
        /// The record does not exist or the caller may not read it; the two read the same.
        /// </summary>
        [EnumMember(Value = "NotFound")]
        NotFound,

        /// <summary>
        /// The caller may read the record but not change it.
        /// </summary>
        [EnumMember(Value = "Forbidden")]
        Forbidden,

        /// <summary>
        /// The write would duplicate a record that must be unique.
        /// </summary>
        [EnumMember(Value = "Conflict")]
        Conflict
    }
}
