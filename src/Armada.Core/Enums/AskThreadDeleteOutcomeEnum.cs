namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Result of a saved Ask thread delete attempt.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskThreadDeleteOutcomeEnum
    {
        /// <summary>The thread and its turns and proposals were removed.</summary>
        Deleted,
        /// <summary>The owner has no such thread.</summary>
        NotFound,
        /// <summary>The thread is not archived.</summary>
        NotArchived,
        /// <summary>The thread holds an active turn, or a turn left without a recorded outcome.</summary>
        OpenTurn,
        /// <summary>The thread holds a proposal that is awaiting a decision or executing.</summary>
        OpenProposal
    }
}
