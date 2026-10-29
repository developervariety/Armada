namespace Armada.Core.Models
{
    /// <summary>
    /// Request to start a battery run.
    /// </summary>
    public class BatteryRunRequest
    {
        #region Public-Members

        /// <summary>
        /// Pack to run.
        /// </summary>
        public string? PackId { get; set; } = null;

        /// <summary>
        /// Captain that must run every task (cpt_ prefix).
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Create the pack's fixture vessel when it does not exist yet. Without it, a missing vessel refuses the run.
        /// </summary>
        public bool CreateFixtureVessel { get; set; } = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryRunRequest()
        {
        }

        #endregion
    }
}
