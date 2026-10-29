namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>Outcome of deleting one saved Ask thread.</summary>
    public sealed class AskThreadDeleteResult
    {
        /// <summary>Whether the thread was deleted, and if not, why it was kept.</summary>
        public AskThreadDeleteOutcomeEnum Outcome { get; set; } = AskThreadDeleteOutcomeEnum.Deleted;

        /// <summary>Reason in words when the thread was not deleted; empty after a delete.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Create an empty result.</summary>
        public AskThreadDeleteResult()
        {
        }

        /// <summary>Create a result.</summary>
        /// <param name="outcome">Delete outcome.</param>
        /// <param name="message">Reason in words, or empty.</param>
        public AskThreadDeleteResult(AskThreadDeleteOutcomeEnum outcome, string message)
        {
            Outcome = outcome;
            Message = message ?? string.Empty;
        }
    }
}
