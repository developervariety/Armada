namespace Armada.Core.Models
{
    /// <summary>The current proposal and HTTP outcome of a human decision.</summary>
    public sealed class AskProposalDecision
    {
        /// <summary>HTTP response status.</summary>
        public int StatusCode { get; set; } = 200;
        /// <summary>Current stored proposal, when visible to the caller.</summary>
        public AskActionProposal? Proposal { get; set; }
        /// <summary>Named refusal or outcome explanation.</summary>
        public string? Message { get; set; }
    }
}
