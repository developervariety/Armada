namespace Armada.Core.Services
{
    /// <summary>Verified text streams retained by the configured Harbor runner.</summary>
    internal sealed class HarborVerificationOutputText
    {
        /// <summary>Verified UTF-8 standard output.</summary>
        public string StandardOutput { get; set; } = System.String.Empty;
        /// <summary>Verified UTF-8 standard error.</summary>
        public string StandardError { get; set; } = System.String.Empty;
    }
}
