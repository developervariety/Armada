namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Review state of one memory proposal. Armada never promotes a proposal: the owner writes
    /// AI-Memory by hand, and an operator dismisses the proposal once it is handled or rejected.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MemoryProposalStateEnum
    {
        /// <summary>The proposal waits for an operator.</summary>
        Open = 0,

        /// <summary>An operator dismissed the proposal, with a reason.</summary>
        Dismissed = 1
    }
}
