namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when a requested stage skip cannot be applied. The dispatch creates nothing.
    /// </summary>
    public class StageSkipRefusedException : InvalidOperationException
    {
        /// <summary>
        /// Machine-readable refusal code, for example <c>stage_skip_judge_refused</c>.
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// The persona name the refusal is about, or null when it concerns the request as a whole.
        /// </summary>
        public string? Persona { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="code">Machine-readable refusal code.</param>
        /// <param name="persona">Persona the refusal is about, or null.</param>
        /// <param name="message">Human-readable reason.</param>
        public StageSkipRefusedException(string code, string? persona, string message) : base(message)
        {
            Code = String.IsNullOrWhiteSpace(code) ? "stage_skip_refused" : code;
            Persona = persona;
        }
    }
}
