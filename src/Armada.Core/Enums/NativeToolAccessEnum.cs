namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How much of a CLI runtime's own shell and file tooling a launch may use.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum NativeToolAccessEnum
    {
        /// <summary>
        /// The runtime keeps its permissive launch defaults: native shell commands and file edits run without a prompt.
        /// Mission launches always use this value.
        /// </summary>
        [EnumMember(Value = "Unrestricted")]
        Unrestricted,

        /// <summary>
        /// The runtime launches without native shell execution and without file writes or edits. Only read-only
        /// native tools and the launch's scoped Armada MCP tools remain.
        /// </summary>
        [EnumMember(Value = "ReadOnly")]
        ReadOnly
    }
}
