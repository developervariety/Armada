namespace Armada.Core.Models
{
    /// <summary>
    /// Machine-readable result returned by `mux endpoint show --output-format json`.
    /// </summary>
    public class MuxEndpointShowResult
    {
        /// <summary>
        /// Structured output contract version.
        /// </summary>
        public int ContractVersion { get; set; } = 0;

        /// <summary>
        /// Whether the command succeeded.
        /// </summary>
        public bool Success { get; set; } = false;

        /// <summary>
        /// Effective config directory used for inspection.
        /// </summary>
        public string ConfigDirectory { get; set; } = String.Empty;

        /// <summary>
        /// Machine-readable error code when the command fails.
        /// </summary>
        public string ErrorCode { get; set; } = String.Empty;

        /// <summary>
        /// Human-readable error message when the command fails.
        /// </summary>
        public string ErrorMessage { get; set; } = String.Empty;

        /// <summary>
        /// Inspected endpoint details.
        /// </summary>
        public MuxEndpointInfo? Endpoint { get; set; } = null;
    }
}
