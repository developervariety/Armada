namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Durable event payload for an unattended lead mode change.
    /// </summary>
    public class LeadModeEventPayload
    {
        #region Public-Members

        /// <summary>
        /// New operating mode.
        /// </summary>
        public LeadOperatingModeEnum Mode { get; set; } = LeadOperatingModeEnum.LegacyPrimary;

        /// <summary>
        /// Authenticated owner or system identity that changed the mode.
        /// </summary>
        public string Actor { get; set; } = String.Empty;

        #endregion
    }
}
