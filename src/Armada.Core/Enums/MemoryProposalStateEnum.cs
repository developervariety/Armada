namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Review state of one memory proposal. Armada never writes AI-Memory: the owner writes the rule by
    /// hand, then an operator records the promotion with the AI-Memory commit that holds it, or dismisses
    /// a proposal that is rejected. Only an Open proposal can move, and it moves once.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MemoryProposalStateEnum
    {
        /// <summary>The proposal waits for an operator.</summary>
        Open = 0,

        /// <summary>An operator dismissed the proposal, with a reason.</summary>
        Dismissed = 1,

        /// <summary>The owner wrote the rule into AI-Memory; the proposal records that commit.</summary>
        Promoted = 2
    }
}
