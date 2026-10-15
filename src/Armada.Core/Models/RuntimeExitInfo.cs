namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Typed outcome of a captain runtime process, decided once when the process exits from its exit code and any
    /// structured provider error the runtime reported while it ran. Quarantine, stall, and crash-loop policy read
    /// <see cref="FailureKind"/>; nothing downstream re-derives it from output text.
    /// </summary>
    public class RuntimeExitInfo
    {
        #region Public-Members

        /// <summary>
        /// Process exit code (null when unavailable).
        /// </summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>
        /// Classified failure kind.
        /// </summary>
        public RuntimeFailureKindEnum FailureKind { get; set; } = RuntimeFailureKindEnum.Crash;

        /// <summary>
        /// The last structured provider error the runtime reported before exiting, if any.
        /// </summary>
        public RuntimeProviderError? ProviderError { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Human-readable summary suitable for a mission failure reason: the provider error's
        /// <see cref="RuntimeProviderError.Reason"/> followed by the exit code when the runtime gave one, otherwise the
        /// exit code and the provider error. Display text only; never parsed.
        /// </summary>
        /// <returns>Summary text.</returns>
        public string Describe()
        {
            string exit = ExitCode.HasValue ? ExitCode.Value.ToString() : "unknown";
            if (ProviderError != null && !String.IsNullOrWhiteSpace(ProviderError.Reason))
            {
                string reason = ProviderError.Reason!.Trim();
                if (!reason.EndsWith(".", StringComparison.Ordinal)) reason += ".";
                return reason + " Agent process exited with code " + exit + ".";
            }

            string text = "Agent process exited with code " + (ExitCode.HasValue ? ExitCode.Value.ToString() : "unknown");
            if (ProviderError != null) text += " (" + ProviderError.ToString() + ")";
            return text;
        }

        #endregion
    }
}
