namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Where a user-defined custom typed decision runs. A custom decision never wires itself into
    /// orchestration code; it runs only at these generic, operator-reachable surfaces, so it can
    /// never take an action the safety contract forbids.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CustomDecisionSurfaceEnum
    {
        /// <summary>
        /// Invoked on demand through the captain- and operator-facing MCP tool. The caller supplies
        /// the context; the decision assembles its state and questions and returns the answer.
        /// </summary>
        [EnumMember(Value = "CaptainTool")]
        CaptainTool,

        /// <summary>
        /// An advisory read of a finished mission's diff and output, the surface a
        /// <see cref="CustomDecisionSeamEnum.MissionDiffFlag"/> binding requires. Nothing runs it
        /// automatically yet: it is reached only through the on-demand tool, whose caller supplies the
        /// mission fields. It never lands, dispatches, or approves.
        /// </summary>
        [EnumMember(Value = "MissionDiff")]
        MissionDiff
    }
}
