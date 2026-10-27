namespace Armada.Core.Models
{

    /// <summary>
    /// Build and test commands of a fixture (informational; the graders run their own commands).
    /// </summary>
    public class BatteryStageChecks
    {
        #region Public-Members

        /// <summary>
        /// Build command.
        /// </summary>
        public string? BuildCommand { get; set; } = null;

        /// <summary>
        /// Test command.
        /// </summary>
        public string? TestCommand { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryStageChecks()
        {
        }

        #endregion
    }
}
