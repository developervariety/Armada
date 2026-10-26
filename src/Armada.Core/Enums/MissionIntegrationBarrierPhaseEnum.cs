namespace Armada.Core.Enums
{
    /// <summary>Lifecycle phase for an Architect integration barrier.</summary>
    public enum MissionIntegrationBarrierPhaseEnum
    {
        /// <summary>The final integration mission is persisted but its branch membership is not yet accepted.</summary>
        Preparing = 1,

        /// <summary>The exact branch terminal membership is accepted and gates assignment.</summary>
        Accepted = 2
    }
}
