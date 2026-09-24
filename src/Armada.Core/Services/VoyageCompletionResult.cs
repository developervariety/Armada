namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Outcome of <see cref="VoyageCompletionRule.ApplyAsync"/>.
    /// </summary>
    public sealed class VoyageCompletionResult
    {
        /// <summary>The voyage after the rule ran; null when it does not exist.</summary>
        public Voyage? Voyage { get; }

        /// <summary>The rule's verdict.</summary>
        public VoyageCompletionVerdict Verdict { get; }

        /// <summary>True when the rule wrote a terminal status.</summary>
        public bool Written => Voyage != null && Verdict.NewStatus != null;

        /// <summary>Instantiate.</summary>
        /// <param name="voyage">Voyage after the rule ran.</param>
        /// <param name="verdict">The rule's verdict.</param>
        public VoyageCompletionResult(Voyage? voyage, VoyageCompletionVerdict verdict)
        {
            Voyage = voyage;
            Verdict = verdict ?? throw new ArgumentNullException(nameof(verdict));
        }
    }
}
