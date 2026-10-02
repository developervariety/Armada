namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A native memory write refused because the calling mission's objective forbids native memory.
    /// The message names the objective and tells the captain to deliver the finding in its report.
    /// </summary>
    public class NativeMemoryForbiddenException : UnauthorizedAccessException
    {
        /// <summary>The reason code a tool returns for this refusal.</summary>
        public const string Code = "native_memory_forbidden";

        /// <summary>Objective whose rule refused the write.</summary>
        public string ObjectiveId { get; }

        /// <summary>Instantiate.</summary>
        /// <param name="objectiveId">Objective whose rule refused the write.</param>
        /// <param name="message">Reason shown to the caller.</param>
        public NativeMemoryForbiddenException(string objectiveId, string message) : base(message)
        {
            ObjectiveId = objectiveId ?? String.Empty;
        }
    }
}
