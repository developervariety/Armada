namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a rendered handoff points the reader at the complete artifact: through a tool the reader actually has.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HandoffPointerKindEnum
    {
        /// <summary>
        /// The reader has Armada MCP tools: the pointer names get_handoff_artifact.
        /// </summary>
        [EnumMember(Value = "Mcp")]
        Mcp,

        /// <summary>
        /// The reader can only read files: the complete artifact is written into the worktree and the pointer names it.
        /// </summary>
        [EnumMember(Value = "File")]
        File,

        /// <summary>
        /// The reader has neither MCP tools nor file access: nothing beyond the rendered block can reach it.
        /// </summary>
        [EnumMember(Value = "None")]
        None
    }
}
