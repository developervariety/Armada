namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Result of probing a workspace for the toolchain a battery task needs.
    /// </summary>
    public class BatteryToolchainCheck
    {
        #region Public-Members

        /// <summary>
        /// Toolchain names whose probe failed.
        /// </summary>
        public List<string> Missing
        {
            get => _Missing;
            set => _Missing = value ?? new List<string>();
        }

        /// <summary>
        /// Why, naming each missing tool and its install hint, or null when nothing is missing.
        /// </summary>
        public string? Reason { get; set; } = null;

        #endregion

        #region Private-Members

        private List<string> _Missing = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryToolchainCheck()
        {
        }

        #endregion
    }
}
