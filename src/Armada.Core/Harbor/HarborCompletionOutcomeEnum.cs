namespace Armada.Core.Harbor
{
    /// <summary>Computed outcome for a confirmed Harbor completion that requeues a mission.</summary>
    public enum HarborCompletionOutcomeEnum
    {
        /// <summary>The attempt was interrupted and consumes the interrupted redispatch budget.</summary>
        Interrupted,

        /// <summary>The attempt failed transiently and the mission returns to the existing retry queue.</summary>
        Transient,

        /// <summary>The attempt is rerouted to a compatible captain and the source captain is quarantined.</summary>
        Rerouted
    }
}
