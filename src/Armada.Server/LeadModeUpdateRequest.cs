namespace Armada.Server
{
    using Armada.Core.Enums;

    /// <summary>
    /// Owner request to change the unattended lead operating mode.
    /// </summary>
    public class LeadModeUpdateRequest
    {
        /// <summary>
        /// New operating mode.
        /// </summary>
        public LeadOperatingModeEnum? Mode { get; set; } = null;
    }
}
