namespace Armada.Core.Models
{
    /// <summary>The owner's decision body for a native tool permission request.</summary>
    public sealed class AskToolPermissionDecisionRequest
    {
        /// <summary>True to remember the decision as a rule for this tool in this thread.</summary>
        public bool Remember { get; set; } = false;
    }
}
