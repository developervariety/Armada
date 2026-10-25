namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Outcome of an atomic saved-turn reservation.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskTurnReservationResultEnum
    {
        /// <summary>A new pending turn was stored and claimed.</summary>
        Created,
        /// <summary>The request id already exists with the same request fields.</summary>
        Existing,
        /// <summary>The request id exists with different request fields.</summary>
        RequestConflict,
        /// <summary>Another turn holds the thread's active claim.</summary>
        ThreadBusy,
        /// <summary>The owner-scoped thread was not found.</summary>
        NotFound
    }
}
