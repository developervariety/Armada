namespace Test.Shared.Infrastructure
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Why a shared case is recorded as not executing in the shared runner.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SharedCaseDispositionKindEnum
    {
        /// <summary>
        /// The case duplicates a legacy runner case that executes in the gate and already asserts the
        /// current contract. The legacy case owns the behaviour.
        /// </summary>
        DuplicateOfLegacyCase,

        /// <summary>
        /// The case asserts behaviour the fork does not implement. The owner decides whether to
        /// implement the behaviour or retire the case.
        /// </summary>
        AwaitingOwnerDecision
    }
}
