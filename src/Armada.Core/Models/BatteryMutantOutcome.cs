namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of one mutant.
    /// </summary>
    public class BatteryMutantOutcome
    {
        #region Public-Members

        /// <summary>
        /// Mutant identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = value ?? String.Empty;
        }

        /// <summary>
        /// Whether the mutant could be applied.
        /// </summary>
        public bool Applied { get; set; } = false;

        /// <summary>
        /// Whether the tests failed with the mutant in place.
        /// </summary>
        public bool Killed { get; set; } = false;

        /// <summary>
        /// Detail.
        /// </summary>
        public string? Reason { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryMutantOutcome()
        {
        }

        #endregion
    }
}
