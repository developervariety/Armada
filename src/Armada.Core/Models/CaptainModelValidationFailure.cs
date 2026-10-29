namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Why a captain's model or runtime configuration failed validation: a machine-readable reason
    /// plus the human-readable message.
    /// </summary>
    public class CaptainModelValidationFailure
    {
        /// <summary>Machine-readable reason.</summary>
        public CaptainModelValidationFailureEnum Reason { get; set; } = CaptainModelValidationFailureEnum.ModelRejected;

        /// <summary>Human-readable message.</summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// Build a failure. The message stays the text the caller already shows.
        /// </summary>
        /// <param name="reason">Machine-readable reason.</param>
        /// <param name="message">Human-readable message.</param>
        /// <returns>The failure.</returns>
        public static CaptainModelValidationFailure Create(CaptainModelValidationFailureEnum reason, string message)
        {
            return new CaptainModelValidationFailure
            {
                Reason = reason,
                Message = message ?? ""
            };
        }
    }
}
