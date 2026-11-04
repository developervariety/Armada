namespace Armada.Core.Models
{
    /// <summary>Body of a terminal landing continuation request.</summary>
    public sealed class TerminalLandingContinuationRequest
    {
        /// <summary>Operator granting the continuation; recorded on the grant and its event.</summary>
        public string? Operator { get; set; } = null;
    }
}
