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

        /// <summary>The exception the voyage completion hook threw after the write, or null.</summary>
        public Exception? HookException { get; }

        /// <summary>The exception writing the <c>voyage.completed</c> event threw after the write, or null.</summary>
        public Exception? EventException { get; }

        /// <summary>Instantiate.</summary>
        /// <param name="voyage">Voyage after the rule ran.</param>
        /// <param name="verdict">The rule's verdict.</param>
        /// <param name="hookException">Exception the voyage completion hook threw, or null.</param>
        /// <param name="eventException">Exception writing the <c>voyage.completed</c> event threw, or null.</param>
        public VoyageCompletionResult(Voyage? voyage, VoyageCompletionVerdict verdict, Exception? hookException = null, Exception? eventException = null)
        {
            Voyage = voyage;
            Verdict = verdict ?? throw new ArgumentNullException(nameof(verdict));
            HookException = hookException;
            EventException = eventException;
        }
    }
}
